using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PazScan.Core.Logging;

/// <summary>
/// One log file a day in <see cref="Directory"/>, rotated when it grows too large, with old files
/// removed. A tray app has no console, and "send me the log" is how a scanner problem on someone
/// else's desk gets diagnosed.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int DefaultKeepDays = 7;
    public const int DefaultMaxFiles = 20;
    public const long DefaultMaxFileBytes = 5 * 1024 * 1024;

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly LogLevel _minimum;
    private readonly long _maxFileBytes;
    private readonly int _keepDays;
    private readonly int _maxFiles;
    private DateTime _fileDate = DateTime.MinValue;
    private int _fileIndex;
    private long _fileSize;

    public FileLoggerProvider(
        string directory,
        LogLevel minimum = LogLevel.Debug,
        long maxFileBytes = DefaultMaxFileBytes,
        int keepDays = DefaultKeepDays,
        int maxFiles = DefaultMaxFiles)
    {
        Directory = directory;
        _minimum = minimum;
        _maxFileBytes = Math.Max(1024, maxFileBytes);
        _keepDays = Math.Max(1, keepDays);
        _maxFiles = Math.Max(1, maxFiles);
        System.IO.Directory.CreateDirectory(directory);
        Cleanup();
    }

    public string Directory { get; }

    public string? CurrentFile
    {
        get
        {
            lock (_gate)
            {
                var now = DateTime.Now;
                if (now.Date != _fileDate)
                {
                    _fileDate = now.Date;
                    _fileIndex = 0;
                    _fileSize = FileLength(FilePath(now, 0));
                }
                return FilePath(now, _fileIndex);
            }
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose() { }

    private string FilePath(DateTime date, int index) =>
        Path.Combine(Directory, $"agent-{date:yyyyMMdd}{(index == 0 ? "" : $"-{index}")}.log");

    private void Write(string line)
    {
        var text = line + Environment.NewLine;
        var bytes = Utf8.GetByteCount(text);
        lock (_gate)
        {
            var now = DateTime.Now;
            if (now.Date != _fileDate)
            {
                _fileDate = now.Date;
                _fileIndex = 0;
                _fileSize = FileLength(FilePath(now, 0));
                Cleanup();
            }

            if (_fileSize > 0 && _fileSize + bytes > _maxFileBytes)
            {
                do { _fileIndex++; } while (File.Exists(FilePath(now, _fileIndex)));
                _fileSize = 0;
                Cleanup();
            }

            try
            {
                File.AppendAllText(FilePath(now, _fileIndex), text, Utf8);
                _fileSize += bytes;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
    }

    private void Cleanup()
    {
        try
        {
            var files = new DirectoryInfo(Directory).GetFiles("agent-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
            var cutoff = DateTime.UtcNow.AddDays(-_keepDays);
            foreach (var file in files.Where(file => file.LastWriteTimeUtc < cutoff))
            {
                try { file.Delete(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }

            foreach (var file in files.Where(file => file.Exists).Skip(_maxFiles))
            {
                try { file.Delete(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= provider._minimum
            && (logLevel >= LogLevel.Warning || !category.StartsWith("Microsoft.", StringComparison.Ordinal));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {logLevel,-11} {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            provider.Write(line);
        }
    }
}
