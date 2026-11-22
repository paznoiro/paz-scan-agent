using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using PazScan.Core.Scanning;

namespace PazScan.Core.Tests;

/// <summary>Drivers that behave like a stuck WIA call: silent, and deaf to cancellation.</summary>
public class ScanWatchdogTests
{
    private static readonly ScanRequest Request = new("fake", "feeder", "color", 200, "a4", false, 85);

    private static void Quick(AgentOptions options)
    {
        options.FirstPageTimeoutSeconds = 0.2;
        options.PageTimeoutSeconds = 0.2;
        options.StopTimeoutSeconds = 0.1;
    }

    private static void OnlyStop(AgentOptions options)
    {
        options.FirstPageTimeoutSeconds = 60;
        options.PageTimeoutSeconds = 60;
        options.StopTimeoutSeconds = 0.1;
    }

    private static async Task WaitForPagesAsync(HttpClient client, string id, int pages)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var status = (await client.GetFromJsonAsync<ScanStatus>($"v1/scans/{id}"))!;
            if (status.Pages.Count >= pages) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("The pages did not arrive.");
    }

    [Fact]
    public async Task A_scanner_that_goes_silent_ends_the_scan_and_keeps_its_pages()
    {
        await using var agent = await AgentFixture.StartAsync(new HangsAfter(2), Quick);
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, Request);
        var ended = await AgentFixture.WaitForEndAsync(client, started.Id);

        Assert.Equal("failed", ended.State);
        Assert.Equal("SCANNER_NOT_RESPONDING", ended.Error!.Code);
        Assert.Contains("after 2 pages", ended.Error.Message);
        Assert.Equal(2, ended.Pages.Count);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"v1/scans/{started.Id}/pages/1")).StatusCode);
    }

    [Fact]
    public async Task A_scanner_that_never_starts_says_so()
    {
        await using var agent = await AgentFixture.StartAsync(new HangsAfter(0), Quick);
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, Request);
        var ended = await AgentFixture.WaitForEndAsync(client, started.Id);

        Assert.Equal("SCANNER_NOT_RESPONDING", ended.Error!.Code);
        Assert.Contains("did not start", ended.Error.Message);
        Assert.Empty(ended.Pages);
    }

    [Fact]
    public async Task Stop_ends_a_scan_whose_driver_never_returns()
    {
        await using var agent = await AgentFixture.StartAsync(new HangsAfter(2), OnlyStop);
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, Request);
        await WaitForPagesAsync(client, started.Id, 2);

        var stopped = await (await client.PostAsync($"v1/scans/{started.Id}/cancel", null)).Content.ReadFromJsonAsync<ScanStatus>();

        Assert.Equal("cancelled", stopped!.State);
        Assert.Null(stopped.Error);
        Assert.Equal(2, stopped.Pages.Count);
    }

    [Fact]
    public async Task A_driver_that_blocks_while_cancelling_does_not_hold_up_Stop()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var agent = await AgentFixture.StartAsync(new HangsAfter(1, release, blockInCancel: true), OnlyStop);
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, Request);
        await WaitForPagesAsync(client, started.Id, 1);

        var clock = Stopwatch.StartNew();
        var stopped = await (await client.PostAsync($"v1/scans/{started.Id}/cancel", null)).Content.ReadFromJsonAsync<ScanStatus>();

        Assert.Equal("cancelled", stopped!.State);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Stop took {clock.Elapsed}");
        release.SetResult();
    }

    [Fact]
    public async Task A_stuck_driver_refuses_a_new_scan_until_it_lets_go_and_its_late_page_is_dropped()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var agent = await AgentFixture.StartAsync(new HangsAfter(2, release), Quick);
        var client = agent.Client();
        var first = await agent.StartScanAsync(client, Request);
        await AgentFixture.WaitForEndAsync(client, first.Id);

        var refused = await client.PostAsJsonAsync("v1/scans", Request);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("SCANNER_STUCK", (await refused.Content.ReadFromJsonAsync<ScanErrorInfo>())!.Code);

        release.SetResult();
        HttpResponseMessage? accepted = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            accepted = await client.PostAsJsonAsync("v1/scans", Request);
            if (accepted.StatusCode == HttpStatusCode.Accepted) break;
            await Task.Delay(10);
        }
        Assert.Equal(HttpStatusCode.Accepted, accepted!.StatusCode);
        var again = (await client.GetFromJsonAsync<ScanStatus>($"v1/scans/{first.Id}"))!;
        Assert.Equal(2, again.Pages.Count);
        Assert.Equal("failed", again.State);
    }

    [Fact]
    public async Task Signs_of_life_keep_a_slow_page_from_timing_out()
    {
        await using var agent = await AgentFixture.StartAsync(new SlowButAlive(TimeSpan.FromMilliseconds(700)), options =>
        {
            options.FirstPageTimeoutSeconds = 0.25;
            options.PageTimeoutSeconds = 0.25;
            options.StopTimeoutSeconds = 0.1;
        });
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, Request);
        var ended = await AgentFixture.WaitForEndAsync(client, started.Id);

        Assert.Equal("done", ended.State);
        Assert.Single(ended.Pages);
    }

    private abstract class FakeBackend : IScanBackend
    {
        public IReadOnlyList<string> Drivers => ["fake"];

        public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([new DeviceInfo("fake", "Fake", "demo", false)]);

        public Task<DeviceCaps?> GetCapsAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult<DeviceCaps?>(null);

        public abstract IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, CancellationToken cancellationToken);
    }

    private sealed class HangsAfter(int pages, TaskCompletionSource? release = null, bool blockInCancel = false) : FakeBackend
    {
        private readonly Task _stuck = release?.Task ?? new TaskCompletionSource().Task;

        public override async IAsyncEnumerable<ScannedImage> ScanAsync(
            ScanSettings settings,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_stuck.IsCompleted) yield break;
            using var blocking = blockInCancel ? cancellationToken.Register(() => _stuck.Wait()) : default;
            for (var index = 0; index < pages; index++)
            {
                await Task.Yield();
                yield return new TestImage();
            }
            // Ignores the token, as a WIA download does while the driver is not calling back.
            await _stuck;
            yield return new TestImage();
        }
    }

    private sealed class SlowButAlive(TimeSpan duration) : FakeBackend, IReportsScanActivity
    {
        public event Action<string>? Activity;

        public override async IAsyncEnumerable<ScannedImage> ScanAsync(
            ScanSettings settings,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < duration)
            {
                Activity?.Invoke("page 1 in progress");
                await Task.Delay(40, cancellationToken);
            }
            yield return new TestImage();
        }
    }

    private sealed class TestImage : ScannedImage
    {
        [SetsRequiredMembers]
        public TestImage()
        {
            Width = 10;
            Height = 20;
            Dpi = 200;
            ContentType = "image/jpeg";
        }

        public override Task SaveAsync(Stream destination, CancellationToken cancellationToken) =>
            destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken).AsTask();

        public override Task SavePreviewAsync(Stream destination, CancellationToken cancellationToken) =>
            destination.WriteAsync(new byte[] { 4 }, cancellationToken).AsTask();
    }
}
