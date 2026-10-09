namespace PazScan.Core.Scanning;

public enum ScanSource { Feeder, Duplex, Flatbed, Auto }

public enum ColorMode { Color, Gray, BlackAndWhite }

public enum PageSide { Front, Back }

/// <param name="PageSize">A key of <see cref="ScanRequestValidation.PageSizes"/>.</param>
public sealed record ScanSettings(
    string DeviceId,
    ScanSource Source,
    ColorMode Color,
    int Dpi,
    string PageSize,
    bool Deskew,
    int Quality);

/// <summary>The thing that actually talks to scanners — NAPS2 on Windows, a stand-in for development.</summary>
public interface IScanBackend
{
    /// <summary>For the status call: which kinds of scanner this build can reach.</summary>
    IReadOnlyList<string> Drivers { get; }

    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken);

    /// <summary>Null when the device is not one this backend knows.</summary>
    Task<DeviceCaps?> GetCapsAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>
    /// One item per page, as the scanner produces them. Throws the driver's own exceptions;
    /// <see cref="ScanErrors"/> turns them into something a person can act on.
    /// </summary>
    IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// One scanned page, not yet on disk. The session writes it out and disposes it straight away, so a
/// two-hundred-page batch never sits in memory.
/// </summary>
public abstract class ScannedImage : IDisposable
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Dpi { get; init; }
    public required string ContentType { get; init; }
    public PageSide? Side { get; init; }
    public string PreviewContentType { get; init; } = "image/jpeg";

    public abstract Task SaveAsync(Stream destination, CancellationToken cancellationToken);

    /// <summary>
    /// A small image (about <see cref="PreviewSize"/> on its long side) for the web app's thumbnails
    /// and its blank-page and separator-sheet checks.
    /// </summary>
    public abstract Task SavePreviewAsync(Stream destination, CancellationToken cancellationToken);

    public const int PreviewSize = 640;

    public virtual void Dispose() => GC.SuppressFinalize(this);
}
