using Microsoft.Extensions.Logging;
using NAPS2.Images.Gdi;
using NAPS2.Scan;
using PazScan.Core;
using PazScan.Core.Logging;
using PazScan.Core.Scanning;

namespace PazScan.Agent.Windows;

/// <summary>
/// The agent on a Windows desk: a tray icon and the local web server Paz talks to.
///
/// <para>A per-user program started at sign-in, not a Windows service. TWAIN and WIA drivers expect
/// the signed-in user's desktop — some open their own progress or settings windows — and a service
/// runs where there is none.</para>
/// </summary>
internal static class Program
{
    /// <summary>One agent per signed-in user; a second start exits at once.</summary>
    internal const string MutexName = "PazScanAgent";

    internal static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Paz Scan Agent");

    [STAThread]
    private static int Main(string[] args)
    {
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

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);
        // Started twice (sign-in and a double-click): the first one is already serving.
        if (!first) return 0;

        ApplicationConfiguration.Initialize();

        var logs = new FileLoggerProvider(Path.Combine(DataDirectory, "logs"));
        var logger = logs.CreateLogger("PazScan.Agent");
        var options = AgentOptions.Load(AppContext.BaseDirectory);
        options.TempPath ??= Path.Combine(DataDirectory, "scans");

        using var scanning = new ScanningContext(new GdiImageContext());
        scanning.Logger = logs.CreateLogger("NAPS2");
        // TWAIN drivers are nearly all 32-bit; this process is 64-bit. NAPS2 runs them in its worker.
        scanning.SetUpWin32Worker();

        var backend = new Naps2Backend(scanning, [Driver.Wia, Driver.Twain, Driver.Escl], logger);
        var app = AgentServer.Build(options, backend, logs);

        string? startError = null;
        try
        {
            app.StartAsync().GetAwaiter().GetResult();
            logger.LogInformation("Listening on 127.0.0.1:{Port}", options.Port);
        }
        catch (IOException error)
        {
            // Usually the port: another program has it.
            startError = $"Port {options.Port} is in use by another program.";
            logger.LogError(error, "Could not listen on port {Port}", options.Port);
        }

        Application.Run(new TrayContext(options, logs.Directory, startError));

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        app.StopAsync(stopping.Token).GetAwaiter().GetResult();
        return 0;
    }
}
