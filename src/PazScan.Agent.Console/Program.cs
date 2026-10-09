// The agent with demo scanners, for working on the Paz web app without a scanner — or without
// Windows. Same API, same port, same origin checks as the real one.
//
//   dotnet run --project src/PazScan.Agent.Console                      generated pages
//   dotnet run --project src/PazScan.Agent.Console -- --pages ~/scans    plus a folder of images

using Microsoft.Extensions.Logging;
using PazScan.Core;
using PazScan.Core.Http;
using PazScan.Core.Scanning;

string? pagesFolder = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--pages") pagesFolder = Path.GetFullPath(args[i + 1]);
}

var options = AgentOptions.Load(AppContext.BaseDirectory);
using var console = new ConsoleLogs();
var app = AgentServer.Build(options, new DemoBackend(pagesFolder), console);

Console.WriteLine($"Paz Scan Agent {AgentApi.Version} (demo scanners) on http://127.0.0.1:{options.Port}");
if (pagesFolder is not null) Console.WriteLine($"Demo folder: {pagesFolder}");
await app.RunAsync();

internal sealed class ConsoleLogs : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

    public void Dispose() { }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Warning || (logLevel >= LogLevel.Information && !category.StartsWith("Microsoft.", StringComparison.Ordinal));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            Console.WriteLine($"{logLevel,-11} {formatter(state, exception)}");
            if (exception is not null) Console.WriteLine(exception.Message);
        }
    }
}
