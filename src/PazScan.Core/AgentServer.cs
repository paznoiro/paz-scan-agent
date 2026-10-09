using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PazScan.Core.Http;
using PazScan.Core.Scanning;
using PazScan.Core.Sessions;

namespace PazScan.Core;

/// <summary>Builds the agent's web server around a backend. The hosts differ only in which backend.</summary>
public static class AgentServer
{
    /// <param name="configure">For tests, which swap Kestrel for an in-memory server.</param>
    public static WebApplication Build(
        AgentOptions options,
        IScanBackend backend,
        ILoggerProvider? logs = null,
        Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Loopback only: nothing else on the network can reach the scanner through this machine.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));

        builder.Logging.ClearProviders();
        if (logs is not null) builder.Logging.AddProvider(logs);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(backend);
        builder.Services.AddSingleton<ScanSessionManager>();

        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseMiddleware<AgentGuard>();
        AgentApi.Map(app);
        return app;
    }
}
