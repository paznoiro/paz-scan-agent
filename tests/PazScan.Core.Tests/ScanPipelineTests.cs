using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using PazScan.Core.Scanning;

namespace PazScan.Core.Tests;

public class ScanPipelineTests
{
    private static readonly ScanRequest Request = new("fake", "feeder", "color", 200, "a4", false, 85);

    [Fact]
    public async Task A_slow_page_does_not_hold_up_the_next_and_pages_stay_in_order()
    {
        // One processor means one page at a time, by design.
        if (Environment.ProcessorCount < 3) return;

        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend(3, async index =>
        {
            if (index == 1) secondStarted.TrySetResult();
            if (index == 0) await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        });

        await using var agent = await AgentFixture.StartAsync(backend);
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, Request);
        var done = await AgentFixture.WaitForEndAsync(client, started.Id);

        Assert.Equal("done", done.State);
        Assert.Equal([0, 1, 2], done.Pages.Select(page => page.Index));
        Assert.Equal([100, 101, 102], done.Pages.Select(page => page.Width));
    }

    [Fact]
    public async Task Stop_keeps_a_page_that_was_already_scanned()
    {
        var processing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend(1, async _ =>
        {
            processing.TrySetResult();
            await finish.Task;
        }, waitForCancelAfterPages: true);

        await using var agent = await AgentFixture.StartAsync(backend);
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, Request);
        await processing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancelling = client.PostAsync($"v1/scans/{started.Id}/cancel", null);
        finish.SetResult();
        await cancelling;
        var ended = await AgentFixture.WaitForEndAsync(client, started.Id);

        Assert.Equal("cancelled", ended.State);
        Assert.Single(ended.Pages);
    }

    [Fact]
    public async Task A_page_that_cannot_be_written_fails_the_scan_and_keeps_those_before_it()
    {
        var firstDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend(3, async index =>
        {
            if (index == 0) firstDone.TrySetResult();
            if (index == 1)
            {
                await firstDone.Task;
                throw new IOException("Disk full");
            }
        });

        await using var agent = await AgentFixture.StartAsync(backend);
        var client = agent.Client();
        var started = await agent.StartScanAsync(client, Request);
        var ended = await AgentFixture.WaitForEndAsync(client, started.Id);

        Assert.Equal("failed", ended.State);
        Assert.Equal(0, Assert.Single(ended.Pages).Index);
    }

    private sealed class FakeBackend(int pages, Func<int, Task> prepare, bool waitForCancelAfterPages = false) : IScanBackend
    {
        public IReadOnlyList<string> Drivers => ["fake"];

        public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([new DeviceInfo("fake", "Fake", "demo", false)]);

        public Task<DeviceCaps?> GetCapsAsync(string deviceId, CancellationToken cancellationToken) =>
            Task.FromResult<DeviceCaps?>(null);

        public async IAsyncEnumerable<ScannedImage> ScanAsync(
            ScanSettings settings,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var index = 0; index < pages; index++)
            {
                await Task.Yield();
                yield return new FakeImage(index, prepare);
            }
            if (waitForCancelAfterPages) await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class FakeImage : ScannedImage
    {
        private readonly int _index;
        private readonly Func<int, Task> _prepare;

        [SetsRequiredMembers]
        public FakeImage(int index, Func<int, Task> prepare)
        {
            _index = index;
            _prepare = prepare;
            ContentType = "image/jpeg";
        }

        public override async Task PrepareAsync(CancellationToken cancellationToken)
        {
            await _prepare(_index);
            Width = 100 + _index;
            Height = 200;
            Dpi = 200;
        }

        public override Task SaveAsync(Stream destination, CancellationToken cancellationToken) =>
            destination.WriteAsync(new byte[] { 1, 2, 3 }, cancellationToken).AsTask();

        public override Task SavePreviewAsync(Stream destination, CancellationToken cancellationToken) =>
            destination.WriteAsync(new byte[] { 4 }, cancellationToken).AsTask();
    }
}
