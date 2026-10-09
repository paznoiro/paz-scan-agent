using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PazScan.Core.Scanning;
using PazScan.Core.Sessions;

namespace PazScan.Core.Http;

/// <summary>
/// The agent's HTTP surface. Version 1, under <c>/v1</c>:
///
/// <code>
/// GET    /v1/status                         name, version, drivers
/// GET    /v1/devices                        every scanner, USB and network
/// GET    /v1/devices/{id}/caps              what one scanner says it can do
/// POST   /v1/scans                          start a scan (one at a time)            → 202
/// GET    /v1/scans/{id}                     state and the pages so far — poll this
/// GET    /v1/scans/{id}/pages/{n}           page n, full resolution
/// GET    /v1/scans/{id}/pages/{n}/preview   page n, small
/// POST   /v1/scans/{id}/cancel              stop feeding; pages so far are kept
/// DELETE /v1/scans/{id}                     stop, and delete its pages
/// </code>
///
/// Errors are <c>{ code, message }</c>.
/// </summary>
public static class AgentApi
{
    public const int ApiVersion = 1;

    public static string Version { get; } =
        typeof(AgentApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AgentApi).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Text($"Paz Scan Agent {Version} is running."));

        var v1 = app.MapGroup("/v1");

        v1.MapGet("/status", (IScanBackend backend) =>
            new AgentStatus("Paz Scan Agent", Version, ApiVersion, RuntimeInformation.OSDescription, backend.Drivers));

        v1.MapGet("/devices", async (IScanBackend backend, CancellationToken cancellationToken) =>
            Results.Ok(await backend.ListDevicesAsync(cancellationToken)));

        v1.MapGet("/devices/{id}/caps", async (string id, IScanBackend backend, CancellationToken cancellationToken) =>
            await backend.GetCapsAsync(id, cancellationToken) is { } caps
                ? Results.Ok(caps)
                : Error(StatusCodes.Status404NotFound, "DEVICE_NOT_FOUND", "The scanner could not be found."));

        v1.MapPost("/scans", (ScanRequest request, ScanSessionManager sessions) =>
        {
            if (!ScanRequestValidation.TryParse(request, out var settings, out var error))
                return Error(StatusCodes.Status400BadRequest, "INVALID_REQUEST", error);
            try
            {
                var session = sessions.Start(settings);
                return Results.Json(session.Snapshot(), statusCode: StatusCodes.Status202Accepted);
            }
            catch (ScanInProgressException exception)
            {
                return Error(StatusCodes.Status409Conflict, "SCAN_IN_PROGRESS", exception.Message);
            }
        });

        v1.MapGet("/scans/{id}", (string id, ScanSessionManager sessions) =>
            sessions.Get(id) is { } session ? Results.Ok(session.Snapshot()) : ScanNotFound());

        v1.MapGet("/scans/{id}/pages/{index:int}", (string id, int index, ScanSessionManager sessions) =>
            PageFile(sessions, id, index, preview: false));

        v1.MapGet("/scans/{id}/pages/{index:int}/preview", (string id, int index, ScanSessionManager sessions) =>
            PageFile(sessions, id, index, preview: true));

        v1.MapPost("/scans/{id}/cancel", async (string id, ScanSessionManager sessions) =>
        {
            if (sessions.Get(id) is not { } session) return ScanNotFound();
            session.Cancel();
            await Task.WhenAny(session.Completion, Task.Delay(TimeSpan.FromSeconds(5)));
            return Results.Ok(session.Snapshot());
        });

        v1.MapDelete("/scans/{id}", async (string id, ScanSessionManager sessions) =>
            await sessions.DeleteAsync(id) ? Results.NoContent() : ScanNotFound());
    }

    private static IResult PageFile(ScanSessionManager sessions, string id, int index, bool preview)
    {
        if (sessions.Get(id) is not { } session) return ScanNotFound();
        if (session.PageFile(index, preview) is not { } file)
            return Error(StatusCodes.Status404NotFound, "PAGE_NOT_FOUND", $"There is no page {index} in this scan.");
        // The bytes never change once published.
        return Results.File(file.Path, file.ContentType, enableRangeProcessing: false);
    }

    private static IResult ScanNotFound() =>
        Error(StatusCodes.Status404NotFound, "SCAN_NOT_FOUND", "That scan is no longer on this computer.");

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ScanErrorInfo(code, message), statusCode: status);
}
