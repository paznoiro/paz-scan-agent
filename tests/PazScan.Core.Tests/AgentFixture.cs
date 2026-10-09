using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using PazScan.Core.Scanning;

namespace PazScan.Core.Tests;

/// <summary>The agent on an in-memory server, with a client that looks like the Paz web app.</summary>
public sealed class AgentFixture : IAsyncDisposable
{
    public const string AllowedOrigin = "https://alzone.zaphrms.com";

    private readonly WebApplication _app;

    private AgentFixture(WebApplication app, string tempPath)
    {
        _app = app;
        TempPath = tempPath;
    }

    public string TempPath { get; }

    public static async Task<AgentFixture> StartAsync(IScanBackend? backend = null)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "pazscan-tests", Guid.NewGuid().ToString("N"));
        var options = new AgentOptions { TempPath = tempPath };
        var app = AgentServer.Build(
            options,
            backend ?? new DemoBackend(pageDelay: TimeSpan.FromMilliseconds(5)),
            configure: builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        return new AgentFixture(app, tempPath);
    }

    /// <summary>
    /// <paramref name="origin"/> null sends none. The base address makes <c>Host</c> what a browser
    /// sends to the real agent.
    /// </summary>
    public HttpClient Client(string? origin = AllowedOrigin, string host = "127.0.0.1")
    {
        var client = _app.GetTestClient();
        client.BaseAddress = new Uri($"http://{host}:{AgentOptions.DefaultPort}/");
        if (origin is not null) client.DefaultRequestHeaders.Add("Origin", origin);
        return client;
    }

    public async Task<ScanStatus> StartScanAsync(HttpClient client, ScanRequest request)
    {
        var response = await client.PostAsJsonAsync("v1/scans", request);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ScanStatus>())!;
    }

    public static async Task<ScanStatus> WaitForEndAsync(HttpClient client, string id)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var status = (await client.GetFromJsonAsync<ScanStatus>($"v1/scans/{id}"))!;
            if (status.State != "scanning") return status;
            await Task.Delay(10);
        }
        throw new TimeoutException("The scan did not finish.");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(TempPath, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
