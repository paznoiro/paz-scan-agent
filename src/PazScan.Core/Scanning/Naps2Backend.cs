using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NAPS2.Images;
using NAPS2.Scan;
using Naps2PageSide = NAPS2.Images.PageSide;

namespace PazScan.Core.Scanning;

/// <summary>
/// Real scanners, through NAPS2.Sdk.
///
/// <para>The host builds the <see cref="ScanningContext"/> because only it knows the platform: on
/// Windows that is a GDI image context plus the 32-bit worker TWAIN drivers need.</para>
/// </summary>
public sealed class Naps2Backend : IScanBackend
{
    /// <summary>Network scanners answer mDNS within a second or two; this waits for the slow ones.</summary>
    private const int NetworkSearchMilliseconds = 4000;

    private readonly ScanningContext _context;
    private readonly ScanController _controller;
    private readonly IReadOnlyList<Driver> _drivers;
    private readonly ILogger _logger;

    // A network scanner's id says nothing about where it is; its connection URI comes from discovery.
    // Kept from the last listing so a scan does not have to search the network again.
    private readonly ConcurrentDictionary<string, ScanDevice> _known = new();

    public Naps2Backend(ScanningContext context, IReadOnlyList<Driver> drivers, ILogger logger)
    {
        _context = context;
        _controller = new ScanController(context) { PropagateErrors = true };
        _drivers = drivers;
        _logger = logger;
    }

    public IReadOnlyList<string> Drivers => _drivers.Select(DriverName).ToList();

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var perDriver = await Task.WhenAll(_drivers.Select(async driver =>
        {
            try
            {
                var options = new ScanOptions { Driver = driver };
                options.EsclOptions.SearchTimeout = NetworkSearchMilliseconds;
                return await _controller.GetDeviceList(options);
            }
            catch (Exception error)
            {
                // A driver that is not installed (no TWAIN DSM, say) must not hide the others.
                _logger.LogWarning(error, "Listing {Driver} scanners failed", driver);
                return new List<ScanDevice>();
            }
        }));

        var devices = new List<DeviceInfo>();
        foreach (var device in perDriver.SelectMany(list => list))
        {
            var id = DeviceIds.Encode(DriverName(device.Driver), device.ID);
            _known[id] = device;
            devices.Add(new DeviceInfo(id, device.Name, DriverName(device.Driver), device.Driver == Driver.Escl));
        }
        return devices;
    }

    public async Task<DeviceCaps?> GetCapsAsync(string deviceId, CancellationToken cancellationToken)
    {
        var device = await ResolveAsync(deviceId, cancellationToken);
        if (device is null) return null;
        try
        {
            var caps = await _controller.GetCaps(device, cancellationToken);
            return ToCaps(caps);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Not every driver can say. The web app then offers every option and the scan itself
            // reports what the scanner refused.
            _logger.LogInformation(error, "Capabilities of {Device} unavailable", device.Name);
            return new DeviceCaps(null, null, null, null, null, null, null, null, null);
        }
    }

    public async IAsyncEnumerable<ScannedImage> ScanAsync(
        ScanSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var device = await ResolveAsync(settings.DeviceId, cancellationToken)
                     ?? throw new ScanFailure("DEVICE_NOT_FOUND", "The scanner could not be found. Check it is on and connected.");

        var (width, height) = ScanRequestValidation.PageSizes[settings.PageSize];
        var options = new ScanOptions
        {
            Device = device,
            Driver = device.Driver,
            PaperSource = settings.Source switch
            {
                ScanSource.Feeder => PaperSource.Feeder,
                ScanSource.Duplex => PaperSource.Duplex,
                ScanSource.Flatbed => PaperSource.Flatbed,
                _ => PaperSource.Auto,
            },
            BitDepth = settings.Color switch
            {
                ColorMode.Gray => BitDepth.Grayscale,
                ColorMode.BlackAndWhite => BitDepth.BlackAndWhite,
                _ => BitDepth.Color,
            },
            Dpi = settings.Dpi,
            PageSize = new PageSize(width, height, PageSizeUnit.Millimetre),
            AutoDeskew = settings.Deskew,
            Quality = settings.Quality,
        };
        options.EsclOptions.SearchTimeout = NetworkSearchMilliseconds;

        _logger.LogInformation("Scanning from {Device} ({Driver}): {Source}, {Color}, {Dpi} dpi, {PageSize}",
            device.Name, device.Driver, settings.Source, settings.Color, settings.Dpi, settings.PageSize);

        await foreach (var image in _controller.Scan(options, cancellationToken))
        {
            yield return new Naps2ScannedImage(_context, image, settings);
        }
    }

    private async Task<ScanDevice?> ResolveAsync(string deviceId, CancellationToken cancellationToken)
    {
        if (_known.TryGetValue(deviceId, out var known)) return known;
        if (!DeviceIds.TryDecode(deviceId, out var driverName, out var nativeId)) return null;
        if (!_drivers.Any(driver => DriverName(driver) == driverName) || nativeId.Length == 0) return null;

        // Restarted since the browser listed the scanners: look again.
        await ListDevicesAsync(cancellationToken);
        return _known.GetValueOrDefault(deviceId);
    }

    private static DeviceCaps ToCaps(ScanCaps caps)
    {
        var perSource = new[] { caps.FlatbedCaps, caps.FeederCaps, caps.DuplexCaps }.OfType<PerSourceCaps>().ToList();
        var union = perSource.Count > 0 ? PerSourceCaps.UnionAll(perSource) : null;
        var dpiCaps = union?.DpiCaps;
        var dpis = dpiCaps?.CommonValues ?? dpiCaps?.Values;
        var area = union?.PageSizeCaps?.ScanArea;
        return new DeviceCaps(
            caps.PaperSourceCaps?.SupportsFlatbed,
            caps.PaperSourceCaps?.SupportsFeeder,
            caps.PaperSourceCaps?.SupportsDuplex,
            dpis is { Count: > 0 } ? dpis.Order().ToList() : null,
            union?.BitDepthCaps?.SupportsColor,
            union?.BitDepthCaps?.SupportsGrayscale,
            union?.BitDepthCaps?.SupportsBlackAndWhite,
            area?.WidthInMm,
            area?.HeightInMm);
    }

    internal static string DriverName(Driver driver) => driver switch
    {
        Driver.Wia => "wia",
        Driver.Twain => "twain",
        Driver.Escl => "escl",
        Driver.Apple => "apple",
        Driver.Sane => "sane",
        _ => "default",
    };

    private sealed class Naps2ScannedImage : ScannedImage
    {
        private readonly ScanningContext _context;
        private readonly ProcessedImage _image;
        // Rendered once: on a deskewed page that is the expensive step.
        private readonly IMemoryImage _rendered;
        private readonly ColorMode _color;
        private readonly int _quality;

        [SetsRequiredMembers]
        public Naps2ScannedImage(ScanningContext context, ProcessedImage image, ScanSettings settings)
        {
            _context = context;
            _image = image;
            _color = settings.Color;
            _quality = settings.Quality;

            _rendered = image.Render();
            Width = _rendered.Width;
            Height = _rendered.Height;
            Dpi = _rendered.HorizontalResolution > 0 ? (int)Math.Round(_rendered.HorizontalResolution) : settings.Dpi;
            // Black and white compresses far better losslessly, and JPEG smears its edges.
            ContentType = _color == ColorMode.BlackAndWhite ? "image/png" : "image/jpeg";
            Side = image.PostProcessingData.PageSide switch
            {
                Naps2PageSide.Front => PageSide.Front,
                Naps2PageSide.Back => PageSide.Back,
                _ => null,
            };
        }

        public override Task SaveAsync(Stream destination, CancellationToken cancellationToken)
        {
            if (_color == ColorMode.BlackAndWhite)
                _rendered.Save(destination, ImageFileFormat.Png, new ImageSaveOptions { PixelFormatHint = ImagePixelFormat.BW1 });
            else
                _rendered.Save(destination, ImageFileFormat.Jpeg, new ImageSaveOptions { Quality = _quality });
            return Task.CompletedTask;
        }

        public override async Task SavePreviewAsync(Stream destination, CancellationToken cancellationToken)
        {
            using var preview = await new ThumbnailRenderer(_context.ImageContext).Render(_image, PreviewSize);
            preview.Save(destination, ImageFileFormat.Jpeg, new ImageSaveOptions { Quality = 80 });
        }

        public override void Dispose()
        {
            _rendered.Dispose();
            _image.Dispose();
            base.Dispose();
        }
    }
}
