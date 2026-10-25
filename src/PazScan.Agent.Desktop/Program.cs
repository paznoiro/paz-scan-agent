using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using PazScan.Core;
using PazScan.Core.Http;
using PazScan.Core.Logging;
using PazScan.Core.Scanning;

namespace PazScan.Agent.Desktop;

/// <summary>
/// The agent on macOS and Linux: the same HTTP API as the Windows tray app, without WinForms.
///
/// <para>It runs in the foreground (a terminal, a LaunchAgent or a systemd user unit), writes the
/// same log files, and can register itself to start at login. In a desktop session it also puts an
/// icon in the macOS menu bar or the Linux tray (<see cref="TrayIcon"/>), as the Windows build does
/// in the tray.</para>
/// </summary>
internal static class Program
{
    /// <summary>
    /// With a tray icon, how often a busy port is tried again. Without one the agent exits instead,
    /// and on macOS launchd starts it again after the same time (the LaunchAgent's ThrottleInterval).
    /// </summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--help") || args.Contains("-h"))
            {
                PrintUsage();
                return 0;
            }
            if (args.Contains("--version"))
            {
                Console.WriteLine(AgentApi.Version);
                return 0;
            }
            if (args.Contains("--register-startup"))
            {
                StartupRegistration.Enable();
                return 0;
            }
            if (args.Contains("--unregister-startup"))
            {
                StartupRegistration.Disable();
                return 0;
            }
            if (args.Contains("--open-logs"))
            {
                Open(AgentPaths.LogDirectory);
                return 0;
            }

            return await RunAgentAsync();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task<int> RunAgentAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("On Windows use PazScan.Agent.Windows (the tray app) instead.");
            return 2;
        }

        Directory.CreateDirectory(AgentPaths.DataDirectory);
        using var instance = SingleInstance.TryAcquire(Path.Combine(AgentPaths.DataDirectory, "agent.lock"));
        if (instance is null)
        {
            Console.WriteLine("Paz Scan Agent is already running.");
            return 0;
        }

        using var logs = new FileLoggerProvider(AgentPaths.LogDirectory);
        var logger = logs.CreateLogger("PazScan.Agent");
        logger.LogInformation("Paz Scan Agent {Version} starting", AgentApi.Version);

        // SIGTERM is how launchd, systemd and the package scripts stop it; SIGINT is Ctrl+C.
        using var shutdown = new CancellationTokenSource();
        void Quit()
        {
            try { shutdown.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            Quit();
        }
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);

        // First, so it shows a problem with the settings too.
        using var tray = TrayIcon.TryStart(logs.Directory, logs.CreateLogger("PazScan.Tray"), Quit);

        AgentOptions options;
        try
        {
            options = AgentOptions.Load(AppContext.BaseDirectory);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Could not read settings");
            var problem = $"Could not read settings: {error.Message}";
            Console.Error.WriteLine(problem);
            return await WaitAfterFailureAsync(tray, problem, Timeout.InfiniteTimeSpan, shutdown.Token) ?? 1;
        }
        options.TempPath ??= AgentPaths.ScanDirectory;

        logger.LogInformation("Platform {Platform}; data {Data}; logs {Logs}",
            RuntimeInformation.OSDescription, AgentPaths.DataDirectory, logs.Directory);
        logger.LogInformation("Settings: port {Port}; allowed origins {Origins}; temp {Temp}",
            options.Port, string.Join(", ", options.EffectiveAllowedOrigins), options.EffectiveTempPath);

        using var scanning = PlatformSetup.CreateScanningContext(logs.CreateLogger("NAPS2"));
        var backend = new Naps2Backend(scanning, PlatformSetup.Drivers, logger);

        WebApplication? app = null;
        try
        {
            string? reported = null;
            while (true)
            {
                string problem;
                var retryAfter = Timeout.InfiniteTimeSpan;
                var again = reported is not null;
                try
                {
                    app = AgentServer.Build(options, backend, logs, builder =>
                    {
                        AddConsoleLogging(builder);
                        // The first failure is in the log already, and the host would log the same
                        // stack trace again on every try.
                        if (again) builder.Logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.None);
                    });
                    await app.StartAsync();
                    break;
                }
                catch (IOException error)
                {
                    // Usually the port: another program has it. That can pass (an agent that is
                    // still stopping, the demo agent), so with an icon to say so, try again.
                    problem = $"Port {options.Port} is in use by another program.";
                    retryAfter = RetryInterval;
                    if (problem != reported) logger.LogError(error, "Could not listen on port {Port}", options.Port);
                }
                catch (Exception error)
                {
                    // A bad setting, such as a malformed allowed origin.
                    problem = error.Message;
                    logger.LogError(error, "Could not start");
                }

                if (app is not null)
                {
                    await app.DisposeAsync();
                    app = null;
                }
                if (problem != reported) Console.Error.WriteLine(problem);
                reported = problem;

                if (await WaitAfterFailureAsync(tray, problem, retryAfter, shutdown.Token) is { } exitCode)
                    return exitCode;
            }

            logger.LogInformation("Listening on 127.0.0.1:{Port}", options.Port);
            tray?.ShowRunning($"Running on port {options.Port}");

            Console.WriteLine($"Paz Scan Agent {AgentApi.Version} on http://127.0.0.1:{options.Port}");
            Console.WriteLine($"Drivers: {string.Join(", ", backend.Drivers)}");
            Console.WriteLine($"Logs: {AgentPaths.LogDirectory}");
            Console.WriteLine("Press Ctrl+C to stop.");

            // SANE's first enumeration can take half a minute. Warm it up now so the web app's
            // first /v1/devices call does not pay the whole cost, and so macOS can ask for local
            // network access before someone presses Scan.
            _ = Task.Run(async () =>
            {
                try
                {
                    await backend.ListDevicesAsync(shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception error)
                {
                    logger.LogDebug(error, "Scanner warm-up failed");
                }
            });

            await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception error)
        {
            logger.LogError(error, "Stopped by an error");
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            logger.LogInformation("Paz Scan Agent stopping");
            if (app is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await app.StopAsync(stop.Token);
                }
                catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
                {
                }
                await app.DisposeAsync();
            }
        }
    }

    private static void AddConsoleLogging(WebApplicationBuilder builder)
    {
        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
    }

    /// <summary>
    /// The agent cannot serve. Without a tray icon that means exit code 1, and launchd or
    /// systemd decides whether to start it again. With one, the icon shows the problem and this
    /// waits: for Quit (0), for the icon to go away (1, as without one), or for
    /// <paramref name="retryAfter"/> to pass (null: try again).
    /// </summary>
    private static async Task<int?> WaitAfterFailureAsync(
        TrayIcon? tray, string problem, TimeSpan retryAfter, CancellationToken shutdown)
    {
        if (tray is null || tray.Exited.IsCompleted) return 1;

        var retrying = retryAfter != Timeout.InfiniteTimeSpan;
        tray.ShowError(retrying ? $"{problem} Trying again…" : problem);
        await Task.WhenAny(Task.Delay(retryAfter, shutdown), tray.Exited);
        if (shutdown.IsCancellationRequested) return 0;
        if (tray.Exited.IsCompleted) return 1;
        return null;
    }

    private static void Open(string path)
    {
        Directory.CreateDirectory(path);
        // Printed as well: over SSH, or on a Linux box without xdg-open, nothing opens.
        Console.WriteLine(path);
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
        }
    }

    private static void PrintUsage() => Console.WriteLine($"""
        Paz Scan Agent {AgentApi.Version} — local scanner bridge for Paz

        Usage:
          PazScanAgent                       run the agent in the foreground
          PazScanAgent --register-startup    start it at login (LaunchAgent / XDG autostart)
          PazScanAgent --unregister-startup  stop starting it at login
          PazScanAgent --open-logs           open the log folder
          PazScanAgent --version
          PazScanAgent --help

        In a desktop session the agent shows an icon in the macOS menu bar or the Linux tray, with
        its status, the live log and Quit.

        Settings are read from appsettings.json beside the executable, then from
        appsettings.json in {AgentPaths.DataDirectory},
        then from PAZSCAN_ environment variables. On Windows, use the tray app instead.
        """);

    private sealed class SingleInstance : IDisposable
    {
        private readonly FileStream _stream;

        private SingleInstance(FileStream stream) => _stream = stream;

        public static SingleInstance? TryAcquire(string path)
        {
            try
            {
                return new SingleInstance(
                    new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException)
            {
                return null;
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}
