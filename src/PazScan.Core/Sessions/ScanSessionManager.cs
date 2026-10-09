using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PazScan.Core.Scanning;

namespace PazScan.Core.Sessions;

public sealed class ScanInProgressException() : Exception("Another scan is still running.");

/// <summary>
/// The scans this agent is holding pages for.
///
/// <para>One at a time: a scanner cannot feed two stacks, and two tabs pressing Scan at once would
/// otherwise interleave their pages. Pages stay until the web app deletes the scan or nobody has
/// asked about it for <see cref="AgentOptions.SessionIdleMinutes"/>.</para>
/// </summary>
public sealed class ScanSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ScanSession> _sessions = new();
    private readonly object _startGate = new();
    private readonly IScanBackend _backend;
    private readonly ILogger<ScanSessionManager> _logger;
    private readonly string _root;
    private readonly TimeSpan _idle;
    private readonly Timer _sweeper;

    public ScanSessionManager(AgentOptions options, IScanBackend backend, ILogger<ScanSessionManager> logger)
    {
        _backend = backend;
        _logger = logger;
        _root = options.EffectiveTempPath;
        _idle = TimeSpan.FromMinutes(options.SessionIdleMinutes);

        // Whatever a previous run left behind belongs to no one now.
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Could not clear {Root}", _root);
        }
        Directory.CreateDirectory(_root);

        _sweeper = new Timer(_ => SweepIdle(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public ScanSession Start(ScanSettings settings)
    {
        lock (_startGate)
        {
            if (_sessions.Values.Any(session => session.IsRunning)) throw new ScanInProgressException();

            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(_root, id);
            Directory.CreateDirectory(directory);
            var session = new ScanSession(id, directory, settings);
            _sessions[id] = session;
            session.Start(_backend, _logger);
            return session;
        }
    }

    public ScanSession? Get(string id)
    {
        if (!_sessions.TryGetValue(id, out var session)) return null;
        session.Touch();
        return session;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) return false;
        session.Cancel();
        try
        {
            // The scan loop may be mid-write; let it stop before its folder goes.
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Scan {Id} did not stop within 10 seconds of being deleted", id);
        }
        TryDeleteDirectory(session.Directory);
        return true;
    }

    internal void SweepIdle()
    {
        var cutoff = DateTimeOffset.UtcNow - _idle;
        foreach (var session in _sessions.Values.Where(session => session.LastTouched < cutoff && !session.IsRunning))
        {
            _logger.LogInformation("Deleting scan {Id}, idle since {Touched}", session.Id, session.LastTouched);
            _ = DeleteAsync(session.Id);
        }
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "Could not delete {Directory}", directory);
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        foreach (var session in _sessions.Values) session.Cancel();
    }
}
