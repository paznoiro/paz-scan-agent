using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using NAPS2.Scan.Exceptions;
using PazScan.Core.Scanning;

namespace PazScan.Core.Tests;

public class AgentApiTests
{
    private static readonly string Stack = DeviceIds.Encode("demo", "stack");
    private static readonly string Jam = DeviceIds.Encode("demo", "jam");

    [Fact]
    public async Task The_root_answers_a_person_typing_the_address_and_says_nothing_else()
    {
        await using var agent = await AgentFixture.StartAsync();
        var response = await agent.Client(origin: null).GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("Paz Scan Agent", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example")]
    [InlineData("https://zaphrms.com.evil.example")]
    [InlineData("null")]
    public async Task Pages_that_are_not_Paz_are_refused(string? origin)
    {
        await using var agent = await AgentFixture.StartAsync();
        var response = await agent.Client(origin).GetAsync("v1/devices");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        var error = await response.Content.ReadFromJsonAsync<ScanErrorInfo>();
        Assert.Equal("ORIGIN_NOT_ALLOWED", error!.Code);
    }

    [Fact]
    public async Task A_rebound_host_name_is_refused_even_from_an_allowed_origin()
    {
        await using var agent = await AgentFixture.StartAsync();
        var response = await agent.Client(host: "attacker.example").GetAsync("v1/devices");
        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
    }

    [Fact]
    public async Task Paz_gets_cors_headers_for_its_own_origin()
    {
        await using var agent = await AgentFixture.StartAsync();
        var response = await agent.Client().GetAsync("v1/status");
        response.EnsureSuccessStatusCode();
        Assert.Equal(AgentFixture.AllowedOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var status = await response.Content.ReadFromJsonAsync<AgentStatus>();
        Assert.Equal(1, status!.ApiVersion);
        Assert.Equal(["demo"], status.Drivers);
    }

    [Fact]
    public async Task The_preflight_allows_json_posts_and_private_network_access()
    {
        await using var agent = await AgentFixture.StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Options, "v1/scans");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        request.Headers.Add("Access-Control-Request-Private-Network", "true");
        var response = await agent.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Equal("content-type", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Private-Network").Single());
    }

    [Fact]
    public async Task Lists_scanners_and_what_they_can_do()
    {
        await using var agent = await AgentFixture.StartAsync();
        var client = agent.Client();
        var devices = await client.GetFromJsonAsync<List<DeviceInfo>>("v1/devices");
        Assert.Equal(["Demo scanner", "Demo scanner (jams)"], devices!.Select(d => d.Name));

        var caps = await client.GetFromJsonAsync<DeviceCaps>($"v1/devices/{Stack}/caps");
        Assert.True(caps!.Duplex);
        Assert.Contains(300, caps.Dpis!);

        var missing = await client.GetAsync($"v1/devices/{DeviceIds.Encode("demo", "nope")}/caps");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_feeder_scan_publishes_every_page_with_a_preview_and_is_gone_once_deleted()
    {
        await using var agent = await AgentFixture.StartAsync();
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, new ScanRequest(Stack, "feeder", "gray", 200, "a4", null, null));
        Assert.Equal("scanning", started.State);

        var done = await AgentFixture.WaitForEndAsync(client, started.Id);
        Assert.Equal("done", done.State);
        Assert.Equal(6, done.Pages.Count);
        Assert.Equal(Enumerable.Range(0, 6), done.Pages.Select(p => p.Index));
        Assert.All(done.Pages, page => Assert.Null(page.Side));
        // A4 at the demo's 100 dpi.
        Assert.Equal((827, 1169, 100), (done.Pages[0].Width, done.Pages[0].Height, done.Pages[0].Dpi));

        var full = await client.GetAsync($"v1/scans/{started.Id}/pages/0");
        Assert.Equal("image/png", full.Content.Headers.ContentType!.MediaType);
        var fullBytes = await full.Content.ReadAsByteArrayAsync();
        Assert.Equal(done.Pages[0].Bytes, fullBytes.Length);
        Assert.Equal((827, 1169), SimplePng.ReadSize(fullBytes));

        var preview = await client.GetByteArrayAsync($"v1/scans/{started.Id}/pages/0/preview");
        var (previewWidth, previewHeight) = SimplePng.ReadSize(preview)!.Value;
        Assert.Equal(ScannedImage.PreviewSize, Math.Max(previewWidth, previewHeight));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"v1/scans/{started.Id}/pages/6")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"v1/scans/{started.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"v1/scans/{started.Id}")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(agent.TempPath, started.Id)));
    }

    [Fact]
    public async Task A_two_sided_scan_says_which_side_each_page_is()
    {
        await using var agent = await AgentFixture.StartAsync();
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, new ScanRequest(Stack, "duplex", "color", 150, "letter", true, 80));
        var done = await AgentFixture.WaitForEndAsync(client, started.Id);
        Assert.Equal(12, done.Pages.Count);
        Assert.Equal(["front", "back", "front", "back"], done.Pages.Take(4).Select(p => p.Side));
    }

    [Fact]
    public async Task A_jam_fails_the_scan_but_keeps_the_pages_before_it()
    {
        await using var agent = await AgentFixture.StartAsync();
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, new ScanRequest(Jam, "feeder", "bw", 300, "a4", false, null));
        var ended = await AgentFixture.WaitForEndAsync(client, started.Id);
        Assert.Equal("failed", ended.State);
        Assert.Equal("PAPER_JAM", ended.Error!.Code);
        Assert.Equal(2, ended.Pages.Count);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"v1/scans/{started.Id}/pages/1")).StatusCode);
    }

    [Fact]
    public async Task One_scan_at_a_time()
    {
        await using var agent = await AgentFixture.StartAsync(new DemoBackend(pageDelay: TimeSpan.FromSeconds(1)));
        var client = agent.Client();
        var first = await agent.StartScanAsync(client, new ScanRequest(Stack, "feeder", null, null, null, null, null));

        var second = await client.PostAsJsonAsync("v1/scans", new ScanRequest(Stack, "feeder", null, null, null, null, null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("SCAN_IN_PROGRESS", (await second.Content.ReadFromJsonAsync<ScanErrorInfo>())!.Code);

        var cancelled = await (await client.PostAsync($"v1/scans/{first.Id}/cancel", null)).Content.ReadFromJsonAsync<ScanStatus>();
        Assert.Equal("cancelled", cancelled!.State);

        // Once it has stopped, the scanner is free again.
        var third = await client.PostAsJsonAsync("v1/scans", new ScanRequest(Stack, "flatbed", null, null, null, null, null));
        Assert.Equal(HttpStatusCode.Accepted, third.StatusCode);
    }

    [Theory]
    [InlineData(null, "feeder", 200, "a4")]
    [InlineData("x", "sideways", 200, "a4")]
    [InlineData("x", "feeder", 20, "a4")]
    [InlineData("x", "feeder", 200, "napkin")]
    public async Task Bad_requests_say_what_is_wrong(string? device, string source, int dpi, string pageSize)
    {
        await using var agent = await AgentFixture.StartAsync();
        var response = await agent.Client().PostAsJsonAsync("v1/scans", new ScanRequest(device, source, "color", dpi, pageSize, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ScanErrorInfo>();
        Assert.Equal("INVALID_REQUEST", error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task A_scanner_that_is_not_there_fails_the_scan_with_a_reason()
    {
        await using var agent = await AgentFixture.StartAsync();
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, new ScanRequest(DeviceIds.Encode("demo", "gone"), "feeder", null, null, null, null, null));
        var ended = await AgentFixture.WaitForEndAsync(client, started.Id);
        Assert.Equal("DEVICE_NOT_FOUND", ended.Error!.Code);
    }

    [Fact]
    public async Task An_empty_feeder_after_some_pages_is_the_end_of_the_stack_not_an_error()
    {
        await using var agent = await AgentFixture.StartAsync(new EmptiesAfter(pages: 2));
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, new ScanRequest(Stack, "feeder", null, null, null, null, null));
        var ended = await AgentFixture.WaitForEndAsync(client, started.Id);
        Assert.Equal("done", ended.State);
        Assert.Equal(2, ended.Pages.Count);
    }

    [Fact]
    public async Task An_empty_feeder_before_any_page_is_reported()
    {
        await using var agent = await AgentFixture.StartAsync(new EmptiesAfter(pages: 0));
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, new ScanRequest(Stack, "feeder", null, null, null, null, null));
        var ended = await AgentFixture.WaitForEndAsync(client, started.Id);
        Assert.Equal("failed", ended.State);
        Assert.Equal("FEEDER_EMPTY", ended.Error!.Code);
    }

    [Fact]
    public async Task A_scanner_that_stops_quietly_when_cancelled_still_reports_cancelled()
    {
        await using var agent = await AgentFixture.StartAsync(new StopsQuietly());
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, new ScanRequest(Stack, "feeder", null, null, null, null, null));
        var cancelled = await (await client.PostAsync($"v1/scans/{started.Id}/cancel", null)).Content.ReadFromJsonAsync<ScanStatus>();
        Assert.Equal("cancelled", cancelled!.State);
    }

    /// <summary>Like NAPS2: a cancelled scan simply ends, with no exception.</summary>
    private sealed class StopsQuietly : IScanBackend
    {
        private readonly DemoBackend _demo = new(pageDelay: TimeSpan.Zero);

        public IReadOnlyList<string> Drivers => ["demo"];

        public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) => _demo.ListDevicesAsync(cancellationToken);

        public Task<DeviceCaps?> GetCapsAsync(string deviceId, CancellationToken cancellationToken) => _demo.GetCapsAsync(deviceId, cancellationToken);

        public async IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var stopped = new TaskCompletionSource();
            await using (cancellationToken.Register(() => stopped.TrySetResult()))
                await stopped.Task;
            yield break;
        }
    }

    /// <summary>Real pages from the demo scanner, then NAPS2's own feeder-empty exception.</summary>
    private sealed class EmptiesAfter(int pages) : IScanBackend
    {
        private readonly DemoBackend _demo = new(pageDelay: TimeSpan.Zero);

        public IReadOnlyList<string> Drivers => ["demo"];

        public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) => _demo.ListDevicesAsync(cancellationToken);

        public Task<DeviceCaps?> GetCapsAsync(string deviceId, CancellationToken cancellationToken) => _demo.GetCapsAsync(deviceId, cancellationToken);

        public async IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var produced = 0;
            await foreach (var image in _demo.ScanAsync(settings, cancellationToken))
            {
                if (produced++ == pages)
                {
                    image.Dispose();
                    break;
                }
                yield return image;
            }
            throw new DeviceFeederEmptyException();
        }
    }
}
