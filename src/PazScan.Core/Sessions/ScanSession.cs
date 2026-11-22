using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PazScan.Core.Scanning;

namespace PazScan.Core.Sessions;

/// <summary>
/// One press of Scan: the pages it has produced so far, on disk, and how it ended.
///
/// <para>The web app polls <see cref="Snapshot"/> and fetches each page as it appears; the agent never
/// pushes. A few pages are processed at a time and their images disposed, so memory stays flat
/// however many sheets go through the feeder.</para>
///
/// <para>A scanner driver can stop calling back and never return, and cancelling does not reach it:
/// WIA only looks at the cancel flag on its next callback. So the feed runs apart from a supervisor
/// that ends the scan when the scanner goes silent or Stop goes unanswered, keeps the pages so far,
/// and leaves the stuck call behind.</para>
/// </summary>
public sealed class ScanSession
{
    /// <summary>
    /// Pages being processed at once. A fast two-sided feeder outruns one encoder, and a queue behind it
    /// makes each later page arrive later; a small limit keeps memory flat on long stacks.
    /// </summary>
    private static readonly int MaxPagesInHand = Math.Clamp(Environment.ProcessorCount - 1, 1, 3);

    private readonly object _gate = new();
    private readonly List<ScanPageInfo> _pages = [];
    private readonly List<string> _previewTypes = [];
    private readonly CancellationTokenSource _cancel = new();
    private readonly ScanTimeouts _timeouts;
    private string _state = "scanning";
    private ScanErrorInfo? _error;
    private Task _published = Task.CompletedTask;
    private bool _abandoned;
    private ILogger? _logger;
    private long _stopRequestedAt;
    private long _lastActivityAt = Stopwatch.GetTimestamp();
    private string _lastActivity = "waiting for the scanner";
    private int _received;

    public ScanSession(string id, string directory, ScanSettings settings, ScanTimeouts? timeouts = null)
    {
        Id = id;
        Directory = directory;
        Settings = settings;
        _timeouts = timeouts ?? ScanTimeouts.Default;
        Touch();
    }

    public string Id { get; }
    public string Directory { get; }
    public ScanSettings Settings { get; }
    public DateTimeOffset LastTouched { get; private set; }

    /// <summary>The scan has ended for the web app, even if a stuck driver call has not returned.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>The driver call has returned, so the scanner is free for another scan.</summary>
    public Task DriverFinished { get; private set; } = Task.CompletedTask;

    public bool IsRunning
    {
        get { lock (_gate) return _state == "scanning"; }
    }

    private bool StopRequested => Interlocked.Read(ref _stopRequestedAt) != 0;

    public void Touch() => LastTouched = DateTimeOffset.UtcNow;

    public ScanStatus Snapshot()
    {
        lock (_gate) return new ScanStatus(Id, _state, _pages.ToList(), _error);
    }

    /// <summary>A published page's file and type; null for a page that does not exist (yet).</summary>
    public (string Path, string ContentType)? PageFile(int index, bool preview)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _pages.Count) return null;
            var type = preview ? _previewTypes[index] : _pages[index].ContentType;
            return (System.IO.Path.Combine(Directory, FileName(index, preview)), type);
        }
    }

    public void Cancel()
    {
        Interlocked.CompareExchange(ref _stopRequestedAt, Stopwatch.GetTimestamp(), 0);
        // A token's callbacks run on the thread that cancels it, and a stuck driver may never return from one.
        _ = Task.Run(() =>
        {
            try
            {
                _cancel.Cancel();
            }
            catch (AggregateException error)
            {
                _logger?.LogWarning(error, "Scan {Id}: the driver failed while being cancelled", Id);
            }
        });
    }

    internal void Start(IScanBackend backend, ILogger logger)
    {
        _logger = logger;
        var reporter = backend as IReportsScanActivity;
        if (reporter is not null) reporter.Activity += NoteActivity;

        var feed = Task.Run(() => FeedAsync(backend, logger));
        DriverFinished = feed.ContinueWith(_ =>
        {
            if (reporter is not null) reporter.Activity -= NoteActivity;
            bool abandoned;
            lock (_gate) abandoned = _abandoned;
            if (abandoned) logger.LogInformation("Scan {Id}: the stuck scanner driver has returned; the scanner is free again", Id);
        }, TaskScheduler.Default);
        Completion = Task.Run(() => SuperviseAsync(feed, logger));
    }

    private void NoteActivity(string activity)
    {
        Volatile.Write(ref _lastActivity, activity);
        Interlocked.Exchange(ref _lastActivityAt, Stopwatch.GetTimestamp());
    }

    private async Task SuperviseAsync(Task<FeedResult> feed, ILogger logger)
    {
        var started = Stopwatch.GetTimestamp();
        ScanErrorInfo? stalled = null;
        while (!feed.IsCompleted)
        {
            await Task.WhenAny(feed, Task.Delay(_timeouts.CheckEvery));
            if (feed.IsCompleted) break;

            var stopAt = Interlocked.Read(ref _stopRequestedAt);
            if (stopAt != 0)
            {
                if (Stopwatch.GetElapsedTime(stopAt) < _timeouts.Stop) continue;
                await AbandonDriverAsync(stalled, logger);
                LogFinished(logger, started);
                return;
            }

            var silent = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivityAt));
            var received = Volatile.Read(ref _received);
            if (silent < (received == 0 ? _timeouts.FirstPage : _timeouts.Page)) continue;

            logger.LogWarning("Scan {Id}: no response from the scanner for {Seconds:0} s after {Pages} page(s). Last activity: {Activity}",
                Id, silent.TotalSeconds, received, Volatile.Read(ref _lastActivity));
            stalled = NotResponding(received);
            Cancel();
        }

        var result = await feed;
        // A driver that reported the end of the stack just as the timer ran out did finish.
        if (stalled is not null && result.State != "done") Finish("failed", stalled);
        else Finish(result.State, result.Error);
        LogFinished(logger, started);
    }

    /// <summary>Ends the scan without the driver. Pages it delivers from now on are dropped.</summary>
    private async Task AbandonDriverAsync(ScanErrorInfo? stalled, ILogger logger)
    {
        Task pending;
        lock (_gate)
        {
            _abandoned = true;
            pending = _published;
        }
        logger.LogWarning("Scan {Id}: the scanner driver did not stop within {Seconds:0.#} s; leaving it behind. Last activity: {Activity}",
            Id, _timeouts.Stop.TotalSeconds, Volatile.Read(ref _lastActivity));

        ScanErrorInfo? writeError = null;
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Scan {Id}: a page could not be finished", Id);
            writeError = ScanErrors.From(error);
        }

        var failure = stalled ?? writeError;
        Finish(failure is null ? "cancelled" : "failed", failure);
    }

    private static ScanErrorInfo NotResponding(int pages) => new(
        "SCANNER_NOT_RESPONDING",
        pages == 0
            ? "The scanner did not start. Check it is on, has paper, and is not in use by another program."
            : $"The scanner stopped responding after {pages} page{(pages == 1 ? "" : "s")}. They are kept. Check the feeder for paper left in it.");

    private async Task<FeedResult> FeedAsync(IScanBackend backend, ILogger logger)
    {
        var started = Stopwatch.GetTimestamp();
        using var slots = new SemaphoreSlim(MaxPagesInHand);
        var nextIndex = 0;
        try
        {
            try
            {
                await foreach (var image in backend.ScanAsync(Settings, _cancel.Token))
                {
                    Interlocked.Increment(ref _received);
                    NoteActivity($"received page {nextIndex + 1}");
                    await slots.WaitAsync();

                    bool handedOver = false, failed;
                    lock (_gate)
                    {
                        failed = _published.IsFaulted;
                        if (!_abandoned && !failed)
                        {
                            var index = nextIndex++;
                            var written = Task.Run(() => WritePageAsync(image, index, slots, logger));
                            _published = PublishInOrderAsync(_published, written);
                            handedOver = true;
                        }
                    }
                    if (handedOver) continue;

                    slots.Release();
                    image.Dispose();
                    if (failed) break;
                    logger.LogWarning("Scan {Id}: dropped a page that arrived after the scan had ended", Id);
                }
            }
            finally
            {
                // A jam or a Stop ends the feed; the pages already scanned are still written and kept.
                Task published;
                lock (_gate) published = _published;
                await published;
            }
            // NAPS2 ends a cancelled scan quietly rather than throwing.
            return new FeedResult(StopRequested ? "cancelled" : "done", null);
        }
        catch (OperationCanceledException) when (StopRequested)
        {
            return new FeedResult("cancelled", null);
        }
        catch (Exception error)
        {
            var info = ScanErrors.From(error);
            int pages;
            lock (_gate) pages = _pages.Count;
            // An empty feeder after some pages is how a stack ends on some drivers, not a failure.
            if (info.Code == "FEEDER_EMPTY" && pages > 0)
            {
                logger.LogInformation("Scan {Id}: feeder empty after {Pages} page(s); treating the stack as finished", Id, pages);
                return new FeedResult("done", null);
            }
            logger.LogWarning(error, "Scan {Id} failed after {Pages} page(s) in {Elapsed:0.0} ms: {Code}",
                Id, pages, Stopwatch.GetElapsedTime(started).TotalMilliseconds, info.Code);
            return new FeedResult("failed", info);
        }
    }

    private async Task<WrittenPage> WritePageAsync(ScannedImage image, int index, SemaphoreSlim slots, ILogger logger)
    {
        try
        {
            using (image)
            {
                var pageStarted = Stopwatch.GetTimestamp();
                // Not the scan's token: Stop ends the feed, and a page already scanned is kept.
                await image.PrepareAsync(CancellationToken.None);

                var full = Path.Combine(Directory, FileName(index, preview: false));
                await using (var stream = File.Create(full))
                    await image.SaveAsync(stream, CancellationToken.None);

                var preview = Path.Combine(Directory, FileName(index, preview: true));
                await using (var stream = File.Create(preview))
                    await image.SavePreviewAsync(stream, CancellationToken.None);

                var fullBytes = new FileInfo(full).Length;
                var previewBytes = new FileInfo(preview).Length;
                var page = new ScanPageInfo(
                    index,
                    image.Width,
                    image.Height,
                    image.Dpi,
                    image.ContentType,
                    fullBytes,
                    image.Side switch { PageSide.Front => "front", PageSide.Back => "back", _ => null });

                logger.LogInformation("Scan {Id} page {Index}: {Width}x{Height} at {Dpi} dpi, {ContentType}, side {Side}, {Bytes} bytes in {File}; preview {PreviewBytes} bytes in {Preview}; {Elapsed:0.0} ms",
                    Id, index, page.Width, page.Height, page.Dpi, page.ContentType, page.Side ?? "single",
                    fullBytes, full, previewBytes, preview, Stopwatch.GetElapsedTime(pageStarted).TotalMilliseconds);
                return new WrittenPage(page, image.PreviewContentType);
            }
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>
    /// Pages are written side by side but appear in scan order, and only once both files are complete,
    /// so a poll never sees a half-written page or a gap.
    /// </summary>
    private async Task PublishInOrderAsync(Task earlier, Task<WrittenPage> written)
    {
        await Task.WhenAll(earlier, written);
        var (page, previewType) = await written;
        lock (_gate)
        {
            _pages.Add(page);
            _previewTypes.Add(previewType);
        }
        NoteActivity($"saved page {page.Index + 1}");
    }

    private readonly record struct WrittenPage(ScanPageInfo Page, string PreviewType);

    private readonly record struct FeedResult(string State, ScanErrorInfo? Error);

    private void LogFinished(ILogger logger, long started)
    {
        int pages;
        long bytes;
        string state;
        lock (_gate)
        {
            pages = _pages.Count;
            bytes = _pages.Sum(page => page.Bytes);
            state = _state;
        }
        logger.LogInformation("Scan {Id} {State}: {Pages} page(s), {Bytes} bytes, {Elapsed:0.0} ms",
            Id, state, pages, bytes, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private void Finish(string state, ScanErrorInfo? error)
    {
        lock (_gate)
        {
            _state = state;
            _error = error;
        }
    }

    private static string FileName(int index, bool preview) => preview ? $"{index:D4}.preview" : $"{index:D4}.page";
}

/// <summary>How long a scanner may stay silent, and how long a Stop may go unanswered.</summary>
public sealed record ScanTimeouts(TimeSpan FirstPage, TimeSpan Page, TimeSpan Stop)
{
    public static ScanTimeouts Default { get; } = From(new AgentOptions());

    public static ScanTimeouts From(AgentOptions options) => new(
        TimeSpan.FromSeconds(options.FirstPageTimeoutSeconds),
        TimeSpan.FromSeconds(options.PageTimeoutSeconds),
        TimeSpan.FromSeconds(options.StopTimeoutSeconds));

    internal TimeSpan CheckEvery
    {
        get
        {
            var shortest = new[] { FirstPage, Page, Stop }.Min();
            var half = shortest / 2;
            return half < TimeSpan.FromMilliseconds(250) ? (half > TimeSpan.FromMilliseconds(5) ? half : TimeSpan.FromMilliseconds(5)) : TimeSpan.FromMilliseconds(250);
        }
    }
}
