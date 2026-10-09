using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace PazScan.Core.Scanning;

/// <summary>
/// Scanners that are not there, for building the web app on a machine without one.
///
/// <list type="bullet">
/// <item><b>Demo scanner</b> feeds a short stack of generated pages, the fourth of them blank; two-sided,
/// most backs come out blank, as they do for single-sided paper.</item>
/// <item><b>Demo scanner (jams)</b> jams after the second page.</item>
/// <item><b>Demo folder</b>, when a folder is given, "scans" the PNG and JPEG files in it in name
/// order — drop in a photo of a printed separator sheet to try the splitting.</item>
/// </list>
/// </summary>
public sealed class DemoBackend(string? pagesFolder = null, TimeSpan? pageDelay = null) : IScanBackend
{
    public const string StackId = "demo|stack";
    public const string JamId = "demo|jam";
    public const string FolderId = "demo|folder";

    /// <summary>Generated pages are drawn at this resolution whatever was asked, to stay small.</summary>
    private const int GeneratedDpi = 100;

    private const int StackSheets = 6;
    private const int BlankSheet = 4;

    private readonly TimeSpan _pageDelay = pageDelay ?? TimeSpan.FromMilliseconds(350);

    public IReadOnlyList<string> Drivers => ["demo"];

    public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = new List<DeviceInfo>
        {
            new(Id(StackId), "Demo scanner", "demo", false),
            new(Id(JamId), "Demo scanner (jams)", "demo", false),
        };
        if (pagesFolder is not null) devices.Add(new(Id(FolderId), "Demo folder", "demo", false));
        return Task.FromResult<IReadOnlyList<DeviceInfo>>(devices);
    }

    public Task<DeviceCaps?> GetCapsAsync(string deviceId, CancellationToken cancellationToken)
    {
        DeviceCaps? caps = Native(deviceId) switch
        {
            StackId or JamId => new DeviceCaps(true, true, true, [100, 150, 200, 300, 600], true, true, true, 216m, 356m),
            FolderId when pagesFolder is not null => new DeviceCaps(false, true, false, null, true, true, true, null, null),
            _ => null,
        };
        return Task.FromResult(caps);
    }

    public async IAsyncEnumerable<ScannedImage> ScanAsync(
        ScanSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var native = Native(settings.DeviceId);
        if (native == FolderId && pagesFolder is not null)
        {
            foreach (var file in Directory.EnumerateFiles(pagesFolder).Order(StringComparer.OrdinalIgnoreCase))
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (extension is not (".png" or ".jpg" or ".jpeg")) continue;
                await Task.Delay(_pageDelay, cancellationToken);
                var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                var size = SimplePng.ReadSize(bytes);
                if (size is null) continue;
                yield return new FileImage(bytes, extension == ".png" ? "image/png" : "image/jpeg", size.Value.Width, size.Value.Height);
            }
            yield break;
        }

        if (native is not (StackId or JamId))
            throw new ScanFailure("DEVICE_NOT_FOUND", "The scanner could not be found. Check it is on and connected.");

        if (settings.Source == ScanSource.Flatbed)
        {
            await Task.Delay(_pageDelay, cancellationToken);
            yield return new GeneratedImage(1, blank: false, side: null, settings.PageSize);
            yield break;
        }

        var duplex = settings.Source == ScanSource.Duplex;
        for (var sheet = 1; sheet <= StackSheets; sheet++)
        {
            if (native == JamId && sheet == 3)
                throw new ScanFailure("PAPER_JAM", "The paper jammed. Clear it and scan the rest.");

            await Task.Delay(_pageDelay, cancellationToken);
            yield return new GeneratedImage(sheet, blank: sheet == BlankSheet, duplex ? PageSide.Front : null, settings.PageSize);
            if (duplex)
            {
                await Task.Delay(_pageDelay, cancellationToken);
                // The second sheet is printed on both sides; every other back is empty.
                yield return new GeneratedImage(sheet, blank: sheet != 2, PageSide.Back, settings.PageSize);
            }
        }
    }

    private static string Id(string native)
    {
        var bar = native.IndexOf('|');
        return DeviceIds.Encode(native[..bar], native[(bar + 1)..]);
    }

    private static string? Native(string deviceId) =>
        DeviceIds.TryDecode(deviceId, out var driver, out var native) ? $"{driver}|{native}" : null;

    private sealed class FileImage : ScannedImage
    {
        private readonly byte[] _bytes;

        [SetsRequiredMembers]
        public FileImage(byte[] bytes, string contentType, int width, int height)
        {
            _bytes = bytes;
            Width = width;
            Height = height;
            // A phone photo carries no useful resolution; call it an A4-wide page.
            Dpi = Math.Max(72, (int)Math.Round(Math.Min(width, height) / 8.27));
            ContentType = contentType;
            PreviewContentType = contentType;
        }

        public override Task SaveAsync(Stream destination, CancellationToken cancellationToken) =>
            destination.WriteAsync(_bytes, cancellationToken).AsTask();

        // The browser scales it down for the thumbnail; this backend has no image library to do it.
        public override Task SavePreviewAsync(Stream destination, CancellationToken cancellationToken) =>
            SaveAsync(destination, cancellationToken);
    }

    private sealed class GeneratedImage : ScannedImage
    {
        private readonly int _number;
        private readonly bool _blank;
        private readonly decimal _widthMm;
        private readonly decimal _heightMm;

        [SetsRequiredMembers]
        public GeneratedImage(int number, bool blank, PageSide? side, string pageSize)
        {
            _number = number;
            _blank = blank;
            (_widthMm, _heightMm) = ScanRequestValidation.PageSizes[pageSize];
            Width = Pixels(_widthMm, GeneratedDpi);
            Height = Pixels(_heightMm, GeneratedDpi);
            Dpi = GeneratedDpi;
            ContentType = "image/png";
            PreviewContentType = "image/png";
            Side = side;
        }

        public override Task SaveAsync(Stream destination, CancellationToken cancellationToken) =>
            destination.WriteAsync(Render(Width, Height, GeneratedDpi), cancellationToken).AsTask();

        public override Task SavePreviewAsync(Stream destination, CancellationToken cancellationToken)
        {
            var scale = (double)PreviewSize / Math.Max(Width, Height);
            var width = (int)Math.Round(Width * scale);
            var height = (int)Math.Round(Height * scale);
            return destination.WriteAsync(Render(width, height, (int)Math.Round(GeneratedDpi * scale)), cancellationToken).AsTask();
        }

        private static int Pixels(decimal millimetres, int dpi) => (int)Math.Round(millimetres / 25.4m * dpi);

        /// <summary>A page of "text": a heading bar, ruled lines, and the page number as a row of blocks.</summary>
        private byte[] Render(int width, int height, int dpi)
        {
            var pixels = new byte[width * height];
            Array.Fill(pixels, (byte)250);
            var random = new Random(_number * 7919 + width);

            // Paper is never perfectly white; a little grain keeps the blank check honest.
            for (var i = 0; i < pixels.Length; i += 37) pixels[i] = (byte)random.Next(228, 250);

            if (!_blank)
            {
                var margin = width / 10;
                Fill(pixels, width, margin, height / 12, width - 2 * margin, Math.Max(2, height / 60), 40);
                var line = Math.Max(4, height / 45);
                for (var y = height / 6; y < height - height / 8; y += line * 2)
                {
                    var length = (int)((width - 2 * margin) * (0.55 + random.NextDouble() * 0.45));
                    Fill(pixels, width, margin, y, length, Math.Max(1, line / 2), 70);
                }
                var block = Math.Max(4, width / 40);
                for (var n = 0; n < _number; n++)
                    Fill(pixels, width, margin + n * block * 2, height - height / 12, block, block, 20);
            }

            return SimplePng.EncodeGray(pixels, width, height, dpi);
        }

        private static void Fill(byte[] pixels, int stride, int x, int y, int w, int h, byte value)
        {
            var height = pixels.Length / stride;
            for (var row = Math.Max(0, y); row < Math.Min(height, y + h); row++)
                Array.Fill(pixels, value, row * stride + Math.Max(0, x), Math.Max(0, Math.Min(stride, x + w) - Math.Max(0, x)));
        }
    }
}
