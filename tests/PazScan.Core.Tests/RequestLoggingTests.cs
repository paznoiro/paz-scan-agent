using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PazScan.Core.Http;

namespace PazScan.Core.Tests;

public class RequestLoggingTests
{
    [Theory]
    [InlineData("GET", "/v1/scans/abc", 200, false)]
    [InlineData("GET", "/v1/scans/abc", 404, true)]
    [InlineData("GET", "/v1/scans/abc/pages/0", 200, true)]
    [InlineData("GET", "/v1/scans/abc/pages/0/preview", 200, true)]
    [InlineData("POST", "/v1/scans", 202, true)]
    [InlineData("POST", "/v1/scans/abc/cancel", 200, true)]
    [InlineData("DELETE", "/v1/scans/abc", 204, true)]
    [InlineData("GET", "/v1/devices", 200, true)]
    public async Task Only_a_successful_status_poll_stays_out_of_the_log(string method, string path, int status, bool logged)
    {
        var logger = new RecordingLogger();
        var guard = new AgentGuard(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }, new AgentOptions(), logger);

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Host = new HostString("127.0.0.1", AgentOptions.DefaultPort);
        context.Request.Headers.Origin = AgentFixture.AllowedOrigin;

        await guard.InvokeAsync(context);

        Assert.Equal(logged, logger.Written > 0);
    }

    /// <summary>Keeps what the agent's log file keeps: Debug and above.</summary>
    private sealed class RecordingLogger : ILogger<AgentGuard>
    {
        public int Written { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Written++;
        }
    }
}
