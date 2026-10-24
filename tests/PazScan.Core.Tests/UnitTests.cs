using Microsoft.Extensions.Logging;
using NAPS2.Scan.Exceptions;
using PazScan.Core.Http;
using PazScan.Core.Logging;
using PazScan.Core.Scanning;

namespace PazScan.Core.Tests;

public class OriginPolicyTests
{
    private static readonly OriginPolicy Defaults = new(AgentOptions.DefaultAllowedOrigins);

    [Theory]
    [InlineData("https://app.zaporion.com")]
    [InlineData("https://a.b.zaporion.com")]
    [InlineData("https://alzone.zaphrms.com")]
    [InlineData("https://shop.vanix.com")]
    [InlineData("https://demohrms.vanix.store")]
    [InlineData("http://127.0.0.1:3006")]
    [InlineData("http://localhost:3000")]
    public void Allows_every_place_Paz_is_served_from(string origin) => Assert.True(Defaults.IsAllowed(origin));

    [Theory]
    [InlineData("https://zaporion.com")] // bare domain: only subdomains are listed
    [InlineData("https://zaphrms.com")] // bare domain: only subdomains are listed
    [InlineData("https://vanix.com")] // bare domain: only subdomains are listed
    [InlineData("https://vanix.store")] // bare domain: only subdomains are listed
    [InlineData("http://app.zaporion.com")] // not https
    [InlineData("https://app.zaporion.com:8443")] // a port the pattern does not name
    [InlineData("https://evilzaporion.com")]
    [InlineData("https://zaporion.com.evil.example")]
    [InlineData("https://localhost:3000")] // loopback is listed over http only
    [InlineData("http://localhost.evil.example:3000")]
    [InlineData("https://app.zaporion.com/path")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(null)]
    public void Refuses_everything_else(string? origin) => Assert.False(Defaults.IsAllowed(origin));

    [Fact]
    public void A_configured_list_with_an_exact_port()
    {
        var policy = new OriginPolicy(["https://files.example.com:8443"]);
        Assert.True(policy.IsAllowed("https://files.example.com:8443"));
        Assert.False(policy.IsAllowed("https://files.example.com"));
    }

    [Theory]
    [InlineData("pazscan")]
    [InlineData("https://paz*.com")]
    [InlineData("https://pazscan:http")]
    public void Malformed_patterns_fail_loudly_at_start_up(string pattern) =>
        Assert.Throws<FormatException>(() => new OriginPolicy([pattern]));
}

public class DeviceIdTests
{
    [Theory]
    [InlineData("wia", "{6BDD1FC6-810F-11D0-BEC7-08002BE2092F}\\0001")]
    [InlineData("escl", "urn:uuid:4509a320-00a0-008f-00b6-002507510eca")]
    [InlineData("twain", "Canon DR-C225 TWAIN")]
    public void Round_trips_and_is_url_safe(string driver, string nativeId)
    {
        var id = DeviceIds.Encode(driver, nativeId);
        Assert.DoesNotContain('/', id);
        Assert.DoesNotContain('+', id);
        Assert.DoesNotContain('=', id);
        Assert.True(DeviceIds.TryDecode(id, out var decodedDriver, out var decodedId));
        Assert.Equal((driver, nativeId), (decodedDriver, decodedId));
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("bm9iYXI")] // "nobar"
    public void Rejects_ids_it_did_not_make(string id) => Assert.False(DeviceIds.TryDecode(id, out _, out _));
}

public class ScanErrorTests
{
    [Fact]
    public void Driver_exceptions_become_codes_a_person_can_act_on()
    {
        Assert.Equal("PAPER_JAM", ScanErrors.From(new DevicePaperJamException()).Code);
        Assert.Equal("FEEDER_EMPTY", ScanErrors.From(new DeviceFeederEmptyException()).Code);
        Assert.Equal("COVER_OPEN", ScanErrors.From(new DeviceCoverOpenException()).Code);
        Assert.Equal("NO_DUPLEX", ScanErrors.From(new NoDuplexSupportException()).Code);
        Assert.Equal("DEVICE_OFFLINE", ScanErrors.From(new DeviceOfflineException()).Code);
        Assert.Equal("SCAN_FAILED", ScanErrors.From(new InvalidOperationException("boom")).Code);
        Assert.Equal("boom", ScanErrors.From(new InvalidOperationException("boom")).Message);
    }
}

public class SimplePngTests
{
    [Fact]
    public void Writes_a_png_whose_size_reads_back()
    {
        var pixels = new byte[30 * 20];
        var png = SimplePng.EncodeGray(pixels, 30, 20, 150);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png.Take(4));
        Assert.Equal((30, 20), SimplePng.ReadSize(png));
    }

    [Fact]
    public void Reads_a_jpeg_size_from_its_frame_header_past_other_segments()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8, // SOI
            0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, // APP0, 2 bytes of payload
            0xFF, 0xC4, 0x00, 0x03, 0x00, // DHT: shares the C0–CF range but is not a frame header
            0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x04, 0xB0, 0x03, 0x20, 0x01, 0x01, 0x11, 0x00, // SOF0: 1200 high, 800 wide
        ];
        Assert.Equal((800, 1200), SimplePng.ReadSize(jpeg));
    }

    [Fact]
    public void Anything_else_has_no_size() => Assert.Null(SimplePng.ReadSize([1, 2, 3, 4, 5, 6]));
}

public sealed class FileLoggerProviderTests
{
    [Fact]
    public void Rotates_a_log_that_reaches_its_size_limit()
    {
        var directory = TestDirectory();
        try
        {
            using var provider = new FileLoggerProvider(directory, LogLevel.Information, maxFileBytes: 1024);
            var logger = provider.CreateLogger("test");
            for (var i = 0; i < 100; i++)
                logger.LogInformation("line {Line} with enough padding to make the file grow quickly", i);

            var files = Directory.GetFiles(directory, "agent-*.log");
            Assert.True(files.Length > 1);
            var all = string.Join(Environment.NewLine, files.Select(File.ReadAllText));
            Assert.Contains("line 0 ", all);
            Assert.Contains("line 99 ", all);
        }
        finally { Delete(directory); }
    }

    [Fact]
    public void Removes_logs_older_than_the_keep_window()
    {
        var directory = TestDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            var old = Path.Combine(directory, "agent-20000101.log");
            File.WriteAllText(old, "old");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));

            using var provider = new FileLoggerProvider(directory, LogLevel.Information, keepDays: 7);

            Assert.False(File.Exists(old));
        }
        finally { Delete(directory); }
    }

    [Fact]
    public void Keeps_only_the_newest_files()
    {
        var directory = TestDirectory();
        try
        {
            Directory.CreateDirectory(directory);
            for (var i = 0; i < 5; i++)
            {
                var path = Path.Combine(directory, $"agent-2026010{i + 1}.log");
                File.WriteAllText(path, $"file {i}");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i));
            }

            using var provider = new FileLoggerProvider(directory, LogLevel.Information, maxFiles: 3);

            Assert.Equal(3, Directory.GetFiles(directory, "agent-*.log").Length);
        }
        finally { Delete(directory); }
    }

    private static string TestDirectory() =>
        Path.Combine(Path.GetTempPath(), "pazscan-log-tests", Guid.NewGuid().ToString("N"));

    private static void Delete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
