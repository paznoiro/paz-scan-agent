using PazScan.Core.Scanning;

namespace PazScan.Core;

// The wire format. The web app's `services/scanAgent.ts` mirrors these by hand, so a field renamed
// here is renamed there too.

public sealed record AgentStatus(string Name, string Version, int ApiVersion, string Platform, IReadOnlyList<string> Drivers);

/// <param name="Id">Opaque; pass it back unchanged. Stable across restarts for USB scanners.</param>
/// <param name="Driver"><c>wia</c>, <c>twain</c>, <c>escl</c> (network) or <c>demo</c>.</param>
public sealed record DeviceInfo(string Id, string Name, string Driver, bool Network);

/// <summary>What a scanner says it can do. A null means it did not say, not that it cannot.</summary>
public sealed record DeviceCaps(
    bool? Flatbed,
    bool? Feeder,
    bool? Duplex,
    IReadOnlyList<int>? Dpis,
    bool? Color,
    bool? Gray,
    bool? BlackAndWhite,
    decimal? MaxWidthMm,
    decimal? MaxHeightMm);

public sealed record ScanRequest(
    string? DeviceId,
    string? Source,
    string? Color,
    int? Dpi,
    string? PageSize,
    bool? Deskew,
    int? Quality);

/// <param name="Side"><c>front</c> or <c>back</c> on a two-sided scan, otherwise null.</param>
public sealed record ScanPageInfo(int Index, int Width, int Height, int Dpi, string ContentType, long Bytes, string? Side);

public sealed record ScanErrorInfo(string Code, string Message);

/// <param name="State"><c>scanning</c>, <c>done</c>, <c>failed</c> or <c>cancelled</c>.</param>
public sealed record ScanStatus(string Id, string State, IReadOnlyList<ScanPageInfo> Pages, ScanErrorInfo? Error);

public static class ScanRequestValidation
{
    public static readonly IReadOnlyDictionary<string, ScanSource> Sources = new Dictionary<string, ScanSource>
    {
        ["feeder"] = ScanSource.Feeder,
        ["duplex"] = ScanSource.Duplex,
        ["flatbed"] = ScanSource.Flatbed,
        ["auto"] = ScanSource.Auto,
    };

    public static readonly IReadOnlyDictionary<string, ColorMode> Colors = new Dictionary<string, ColorMode>
    {
        ["color"] = ColorMode.Color,
        ["gray"] = ColorMode.Gray,
        ["bw"] = ColorMode.BlackAndWhite,
    };

    /// <summary>Paper sizes, in millimetres (width × height, portrait).</summary>
    public static readonly IReadOnlyDictionary<string, (decimal Width, decimal Height)> PageSizes =
        new Dictionary<string, (decimal, decimal)>
        {
            ["a4"] = (210m, 297m),
            ["a5"] = (148m, 210m),
            ["a3"] = (297m, 420m),
            ["letter"] = (215.9m, 279.4m),
            ["legal"] = (215.9m, 355.6m),
        };

    public static bool TryParse(ScanRequest request, out ScanSettings settings, out string error)
    {
        settings = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(request.DeviceId))
        {
            error = "Choose a scanner.";
            return false;
        }
        if (!Sources.TryGetValue(request.Source ?? "feeder", out var source))
        {
            error = $"Unknown paper source '{request.Source}'.";
            return false;
        }
        if (!Colors.TryGetValue(request.Color ?? "color", out var color))
        {
            error = $"Unknown colour mode '{request.Color}'.";
            return false;
        }
        var dpi = request.Dpi ?? 200;
        if (dpi is < 50 or > 1200)
        {
            error = "Resolution must be between 50 and 1200 dpi.";
            return false;
        }
        var pageSize = request.PageSize ?? "a4";
        if (!PageSizes.ContainsKey(pageSize))
        {
            error = $"Unknown page size '{request.PageSize}'.";
            return false;
        }
        var quality = request.Quality ?? 85;
        if (quality is < 10 or > 100)
        {
            error = "JPEG quality must be between 10 and 100.";
            return false;
        }

        settings = new ScanSettings(request.DeviceId, source, color, dpi, pageSize, request.Deskew ?? true, quality);
        return true;
    }
}
