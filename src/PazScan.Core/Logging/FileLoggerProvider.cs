using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PazScan.Core.Logging;

/// <summary>
/// One log file a day in <see cref="Directory"/>, a week of them kept. A tray app has no console, and
/// "send me the log" is how a scanner problem on someone else's desk gets diagnosed.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int KeepDays = 7;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly LogLevel _minimum;

    public FileLoggerProvider(string directory, LogLevel minimum = LogLevel.Information)
    {
        Directory = directory;
        _minimum = minimum;
        System.IO.Directory.CreateDirectory(directory);
        foreach (var old in new DirectoryInfo(directory).GetFiles("agent-*.log")
                     .Where(file => file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-KeepDays)))
        {
            try { old.Delete(); } catch (IOException) { }
        }
    }

    public string Directory { get; }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose() { }

    private void Write(string line)
    {
        var path = Path.Combine(Directory, $"agent-{DateTime.Now:yyyyMMdd}.log");
        lock (_gate)
        {
            try { File.AppendAllText(path, line + Environment.NewLine); }
            catch (IOException) { }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= provider._minimum
            // ASP.NET Core's own request chatter would drown the scanner's story.
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
