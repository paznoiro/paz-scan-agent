using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using PazScan.Core.Http;

namespace PazScan.Agent.Desktop;

/// <summary>
/// The agent's icon in the macOS menu bar or the Linux tray, as the tray icon is on Windows: it
/// shows that the agent is running, or why not, opens the live log, and quits the agent.
///
/// <para>The icon is a separate small program beside the agent, so the agent needs no UI toolkit:
/// on macOS <c>PazScanMenuBar</c>, built with AppKit from <c>Tray/macos/*.swift</c>; on Linux
/// <c>PazScanTray.py</c>, GTK 3 and AppIndicator through python3. Both talk to the agent over their
/// stdin and stdout (the messages are listed at the top of each). When the agent exits, the
/// helper's stdin closes and it exits too; when someone chooses Quit, the helper says <c>quit</c>
/// and the agent stops. Without a desktop session (a server, SSH) there is no icon, and the agent
/// runs as it would without one.</para>
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly Action _quit;
    private readonly Lock _gate = new();
    private volatile bool _disposed;

    private TrayIcon(Process process, ILogger logger, Action quit)
    {
        _process = process;
        _logger = logger;
        _quit = quit;
        _ = Task.Run(ReadErrorsAsync);
        Exited = Task.Run(ReadMessagesAsync);
    }

    /// <summary>"menu bar icon" or "tray icon", for the log.</summary>
    private static string Name => OperatingSystem.IsMacOS() ? "menu bar icon" : "tray icon";

    /// <summary>Completes when the helper has exited, for whatever reason.</summary>
    public Task Exited { get; }

    /// <summary>Starts the icon, or returns null when there is none to start.</summary>
    /// <param name="quit">Called when someone quits the agent from the icon's menu.</param>
    public static TrayIcon? TryStart(string logDirectory, ILogger logger, Action quit)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsMacOS())
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "PazScanMenuBar");
            if (!File.Exists(helper))
            {
                logger.LogWarning("No {Icon}: {Path} is missing", Name, helper);
                return null;
            }
            start = new ProcessStartInfo(helper);
        }
        else if (OperatingSystem.IsLinux())
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                logger.LogInformation("No {Icon}: not in a desktop session (neither DISPLAY nor WAYLAND_DISPLAY is set)", Name);
                return null;
            }
            var script = Path.Combine(AppContext.BaseDirectory, "PazScanTray.py");
            if (!File.Exists(script))
            {
                logger.LogWarning("No {Icon}: {Path} is missing", Name, script);
                return null;
            }
            start = new ProcessStartInfo("python3");
            start.ArgumentList.Add(script);
        }
        else
        {
            return null;
        }

        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardInputEncoding = Utf8;
        start.StandardOutputEncoding = Utf8;
        start.StandardErrorEncoding = Utf8;
        start.ArgumentList.Add("--version");
        start.ArgumentList.Add(AgentApi.Version);
        start.ArgumentList.Add("--logs");
        start.ArgumentList.Add(logDirectory);

        try
        {
            var process = Process.Start(start);
            if (process is not null) return new TrayIcon(process, logger, quit);
            logger.LogWarning("Could not start the {Icon}", Name);
        }
        catch (Exception error) when (error is Win32Exception or IOException)
        {
            // On Linux, usually no python3.
            logger.LogWarning("Could not start the {Icon} ({File}): {Reason}", Name, start.FileName, error.Message);
        }
        return null;
    }

    public void ShowRunning(string text) => Send($"state running {text}");

    public void ShowError(string text) => Send($"state error {text}");

    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            // The helper exits when its stdin closes.
            try { _process.StandardInput.Close(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
        try
        {
            if (!_process.WaitForExit(2000)) _process.Kill();
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        _process.Dispose();
    }

    private void Send(string message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _process.StandardInput.WriteLine(message.ReplaceLineEndings(" "));
                _process.StandardInput.Flush();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The helper has gone; ReadMessagesAsync logs that.
            }
        }
    }

    private async Task ReadMessagesAsync()
    {
        var quit = false;
        var failed = false;
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                var (kind, text) = line.IndexOf(' ') is var space and >= 0
                    ? (line[..space], line[(space + 1)..])
                    : (line, "");
                switch (kind)
                {
                    case "ready":
                        _logger.LogInformation("Showing the {Icon}", Name);
                        break;
                    case "quit":
                        quit = true;
                        _logger.LogInformation("Quit from the {Icon}", Name);
                        _quit();
                        break;
                    case "warn":
                        _logger.LogWarning("The {Icon}: {Message}", Name, text);
                        break;
                    case "fail":
                        failed = true;
                        _logger.LogWarning("No {Icon}: {Reason}", Name, text);
                        break;
                }
            }
            await _process.WaitForExitAsync();
            if (!quit && !failed && !_disposed)
                _logger.LogWarning("The {Icon} closed (exit code {Code})", Name, _process.ExitCode);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Disposed while reading: the agent is stopping.
        }
    }

    /// <summary>The toolkit's complaints, such as having no window server to talk to.</summary>
    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line)) _logger.LogDebug("{Line}", line);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}
