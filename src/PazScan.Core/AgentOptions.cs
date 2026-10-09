using Microsoft.Extensions.Configuration;

namespace PazScan.Core;

/// <summary>
/// Where the agent listens and who may talk to it.
///
/// <para>Read from <c>appsettings.json</c> beside the executable, then <c>PAZSCAN_</c> environment
/// variables (<c>PAZSCAN_Agent__Port=47400</c>). The installer ships no settings file, so a plain
/// install runs on these defaults.</para>
/// </summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>The port the Paz web app looks for. Changing it here means changing it there.</summary>
    public const int DefaultPort = 47316;

    /// <summary>
    /// Every host the Paz web app is served from. <c>*.</c> stands for one or more subdomain labels, so a
    /// tenant's own subdomain is covered; <c>:*</c> for any port, which is what local development needs.
    /// Add the real Paz domains here.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAllowedOrigins =
    [
        "https://paznoiro.com",
        "https://*.paznoiro.com",
        "http://localhost:*",
        "http://127.0.0.1:*",
    ];

    public int Port { get; set; } = DefaultPort;

    /// <summary>Replaces <see cref="DefaultAllowedOrigins"/> when set; it does not add to them.</summary>
    public string[]? AllowedOrigins { get; set; }

    /// <summary>Where scanned pages wait until the web app has fetched them. Emptied at start-up.</summary>
    public string? TempPath { get; set; }

    /// <summary>A scan nobody has asked about for this long is deleted, pages and all.</summary>
    public int SessionIdleMinutes { get; set; } = 120;

    public IReadOnlyList<string> EffectiveAllowedOrigins =>
        AllowedOrigins is { Length: > 0 } ? AllowedOrigins : DefaultAllowedOrigins;

    public string EffectiveTempPath =>
        TempPath ?? Path.Combine(Path.GetTempPath(), "PazScanAgent");

    public static AgentOptions Load(string baseDirectory)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("PAZSCAN_")
            .Build();
        var options = new AgentOptions();
        configuration.GetSection(Section).Bind(options);
        return options;
    }
}
