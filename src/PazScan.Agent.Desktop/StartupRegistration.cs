using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace PazScan.Agent.Desktop;

/// <summary>
/// Start at sign-in: a LaunchAgent on macOS, an XDG autostart entry on Linux.
///
/// <para>The installers register the agent for every user (<c>/Library/LaunchAgents</c>,
/// <c>/etc/xdg/autostart</c>). Without one, <c>--register-startup</c> writes a per-user entry that
/// points at the current executable, so move the agent and register again. Turning a package's
/// entry off is a per-user override, never a second entry under the same name.</para>
/// </summary>
internal static class StartupRegistration
{
    private const string MacLabel = "com.paznoiro.pazscanagent";
    private const string LinuxDesktopFile = "paz-scan-agent.desktop";

    private const string MacSystemPlistPath = $"/Library/LaunchAgents/{MacLabel}.plist";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void Enable()
    {
        if (OperatingSystem.IsMacOS()) EnableMac();
        else if (OperatingSystem.IsLinux()) EnableLinux();
        else throw new PlatformNotSupportedException("Start at login is only implemented for macOS and Linux.");
    }

    public static void Disable()
    {
        if (OperatingSystem.IsMacOS()) DisableMac();
        else if (OperatingSystem.IsLinux()) DisableLinux();
        else throw new PlatformNotSupportedException("Start at login is only implemented for macOS and Linux.");
    }

    /// <summary>The apphost, not the <c>dotnet</c> muxer that may have launched it in development.</summary>
    public static string ExecutablePath
    {
        get
        {
            var process = Environment.ProcessPath;
            if (process is not null
                && !Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return process;
            }

            var appHost = Path.Combine(AppContext.BaseDirectory,
                OperatingSystem.IsWindows() ? "PazScanAgent.exe" : "PazScanAgent");
            return File.Exists(appHost) ? appHost : process ?? appHost;
        }
    }

    // --- macOS ------------------------------------------------------------------------------------

    private static string MacService => $"gui/{GetUid()}/{MacLabel}";

    private static string MacUserPlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", $"{MacLabel}.plist");

    private static void EnableMac()
    {
        string plist;
        if (File.Exists(MacSystemPlistPath))
        {
            // Installed by the package: that LaunchAgent already starts it for every user.
            if (File.Exists(MacUserPlistPath)) File.Delete(MacUserPlistPath);
            plist = MacSystemPlistPath;
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MacUserPlistPath)!);
            File.WriteAllText(MacUserPlistPath, MacPlist(ExecutablePath), Utf8);
            plist = MacUserPlistPath;
        }

        // Clears an earlier --unregister-startup, then replaces a job that is already loaded so it
        // runs the executable the plist names now. Errors are not fatal: the plist still starts it
        // at the next login, which is the part that matters when this is run over SSH.
        Run("launchctl", "enable", MacService);
        Run("launchctl", "bootout", MacService);
        var bootstrap = Bootstrap(plist);
        if (bootstrap.Code == 0)
        {
            Console.WriteLine("Paz Scan Agent is running and will start at login.");
        }
        else
        {
            Console.WriteLine("Paz Scan Agent will start at login, but it could not be started now.");
            if (!string.IsNullOrWhiteSpace(bootstrap.Error)) Console.WriteLine(bootstrap.Error.Trim());
        }
    }

    private static void DisableMac()
    {
        Run("launchctl", "bootout", MacService);
        if (File.Exists(MacUserPlistPath)) File.Delete(MacUserPlistPath);
        // The package's LaunchAgent stays for other users; launchd remembers this one's opt-out.
        if (File.Exists(MacSystemPlistPath)) Run("launchctl", "disable", MacService);
        Console.WriteLine("Paz Scan Agent has stopped and will no longer start at login.");
    }

    /// <summary>
    /// Straight after a <c>bootout</c>, launchd may still be tearing the old job down and refuse with
    /// "5: Input/output error"; a moment later the same call works.
    /// </summary>
    private static (int Code, string Error) Bootstrap(string plist)
    {
        var domain = $"gui/{GetUid()}";
        var result = Run("launchctl", "bootstrap", domain, plist);
        for (var attempt = 1; attempt < 5 && result.Code != 0; attempt++)
        {
            Thread.Sleep(500);
            result = Run("launchctl", "bootstrap", domain, plist);
        }
        return result;
    }

    /// <summary>Kept in step with <c>installer/macos/com.paznoiro.pazscanagent.plist</c>.</summary>
    private static string MacPlist(string executable) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{MacLabel}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{SecurityElement.Escape(executable)}</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
          <key>KeepAlive</key>
          <dict>
            <key>SuccessfulExit</key>
            <false/>
          </dict>
          <key>ThrottleInterval</key>
          <integer>10</integer>
          <key>ProcessType</key>
          <string>Interactive</string>
        </dict>
        </plist>

        """;

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    // --- Linux ------------------------------------------------------------------------------------

    private static string LinuxAutostartPath
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }
            return Path.Combine(configHome, "autostart", LinuxDesktopFile);
        }
    }

    /// <summary>An entry the package put in <c>/etc/xdg/autostart</c> (or another XDG config dir).</summary>
    private static bool LinuxSystemEntryExists
    {
        get
        {
            var dirs = Environment.GetEnvironmentVariable("XDG_CONFIG_DIRS");
            if (string.IsNullOrWhiteSpace(dirs)) dirs = "/etc/xdg";
            return dirs.Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Any(dir => File.Exists(Path.Combine(dir, "autostart", LinuxDesktopFile)));
        }
    }

    private static void EnableLinux()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LinuxAutostartPath)!);
        // A user entry with the package's file name takes its place, so this also undoes a
        // Hidden=true opt-out and points the login start at this executable.
        File.WriteAllText(LinuxAutostartPath, LinuxDesktopEntry(hidden: false), Utf8);
        Console.WriteLine("Paz Scan Agent will start at login. To start it now, run:");
        Console.WriteLine($"  {ExecutablePath}");
    }

    private static void DisableLinux()
    {
        if (LinuxSystemEntryExists)
        {
            // Deleting the user entry alone would let the package's entry start it again.
            Directory.CreateDirectory(Path.GetDirectoryName(LinuxAutostartPath)!);
            File.WriteAllText(LinuxAutostartPath, LinuxDesktopEntry(hidden: true), Utf8);
        }
        else if (File.Exists(LinuxAutostartPath))
        {
            File.Delete(LinuxAutostartPath);
        }
        Console.WriteLine("Paz Scan Agent will no longer start at login.");
    }

    private static string LinuxDesktopEntry(bool hidden) => $"""
        [Desktop Entry]
        Type=Application
        Name=Paz Scan Agent
        Comment=Local scanner bridge for Paz
        Exec={QuoteExec(ExecutablePath)}
        Terminal=false
        NoDisplay=true
        {(hidden ? "Hidden=true" : "X-GNOME-Autostart-enabled=true")}

        """;

    /// <summary>
    /// One argument of a desktop entry's <c>Exec</c> key. Inside quotes, <c>" ` $ \</c> take a
    /// backslash; the value is then a string, whose own escaping doubles every backslash again; and
    /// <c>%</c> starts a field code, so it is doubled.
    /// </summary>
    private static string QuoteExec(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(c);
        }
        quoted.Append('"');
        return quoted.ToString().Replace("\\", "\\\\").Replace("%", "%%");
    }

    // --- Both -------------------------------------------------------------------------------------

    private static (int Code, string Error) Run(string file, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null) return (-1, $"{file} could not be started.");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return (-1, $"{file} did not finish in time.");
            }
            _ = stdout.Result;
            return (process.ExitCode, stderr.Result);
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            return (-1, error.Message);
        }
    }
}
