using Microsoft.Extensions.Logging;
using PazScan.Core.Scanning;

namespace PazScan.Core.Sessions;

/// <summary>
/// One press of Scan: the pages it has produced so far, on disk, and how it ended.
///
/// <para>The web app polls <see cref="Snapshot"/> and fetches each page as it appears; the agent never
/// pushes. Pages are written out one at a time and their images disposed, so memory stays flat
/// however many sheets go through the feeder.</para>
/// </summary>
public sealed class ScanSession
{
    private readonly object _gate = new();
    private readonly List<ScanPageInfo> _pages = [];
    private readonly List<string> _previewTypes = [];
    private readonly CancellationTokenSource _cancel = new();
    private string _state = "scanning";
    private ScanErrorInfo? _error;

    public ScanSession(string id, string directory, ScanSettings settings)
    {
        Id = id;
        Directory = directory;
        Settings = settings;
        Touch();
    }

    public string Id { get; }
    public string Directory { get; }
    public ScanSettings Settings { get; }
    public DateTimeOffset LastTouched { get; private set; }
    public Task Completion { get; private set; } = Task.CompletedTask;

    public bool IsRunning
    {
        get { lock (_gate) return _state == "scanning"; }
    }

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

    public void Cancel() => _cancel.Cancel();

    internal void Start(IScanBackend backend, ILogger logger)
    {
        Completion = Task.Run(() => RunAsync(backend, logger));
    }

    private async Task RunAsync(IScanBackend backend, ILogger logger)
    {
        try
        {
            await foreach (var image in backend.ScanAsync(Settings, _cancel.Token))
            {
                using (image)
                {
                    int index;
                    lock (_gate) index = _pages.Count;

                    var full = Path.Combine(Directory, FileName(index, preview: false));
                    await using (var stream = File.Create(full))
                        await image.SaveAsync(stream, _cancel.Token);

                    var preview = Path.Combine(Directory, FileName(index, preview: true));
                    await using (var stream = File.Create(preview))
                        await image.SavePreviewAsync(stream, _cancel.Token);

                    var page = new ScanPageInfo(
                        index,
                        image.Width,
                        image.Height,
                        image.Dpi,
                        image.ContentType,
                        new FileInfo(full).Length,
                        image.Side switch { PageSide.Front => "front", PageSide.Back => "back", _ => null });
                    // Published only once both files are complete, so a poll never sees a half-written page.
                    lock (_gate)
                    {
                        _pages.Add(page);
                        _previewTypes.Add(image.PreviewContentType);
                    }
                }
            }
            Finish("done", null);
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            Finish("cancelled", null);
        }
        catch (Exception error)
        {
            var info = ScanErrors.From(error);
            int pages;
            lock (_gate) pages = _pages.Count;
            // An empty feeder after some pages is how a stack ends on some drivers, not a failure.
            if (info.Code == "FEEDER_EMPTY" && pages > 0)
            {
                Finish("done", null);
                return;
            }
            logger.LogWarning(error, "Scan {Id} failed after {Pages} pages: {Code}", Id, pages, info.Code);
            Finish("failed", info);
        }
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
