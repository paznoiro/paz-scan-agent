namespace PazScan.Core;

/// <summary>
/// Where the agent keeps its logs and the pages waiting to be fetched.
///
/// <para><see cref="Environment.SpecialFolder.LocalApplicationData"/> is the platform's own place:
/// <c>%LOCALAPPDATA%</c> on Windows, <c>~/Library/Application Support</c> on macOS and
/// <c>~/.local/share</c> (or <c>$XDG_DATA_HOME</c>) on Linux.</para>
/// </summary>
public static class AgentPaths
{
    public const string FolderName = "Paz Scan Agent";

    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string ScanDirectory => Path.Combine(DataDirectory, "scans");

    private static string ResolveDataDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            // A container or service account may have no HOME/XDG_DATA_HOME.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) home = Path.GetTempPath();
            root = OperatingSystem.IsWindows()
                ? Path.Combine(home, "AppData", "Local")
                : OperatingSystem.IsMacOS()
                    ? Path.Combine(home, "Library", "Application Support")
                    : Path.Combine(home, ".local", "share");
        }
        return Path.Combine(root, FolderName);
    }
}
