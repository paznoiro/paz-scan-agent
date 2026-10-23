using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PazScan.Core.Http;

/// <summary>
/// Every request passes two checks before it reaches an endpoint.
///
/// <list type="number">
/// <item><b>Host</b> must be the loopback address the agent listens on. A site that points its own
/// DNS name at 127.0.0.1 (DNS rebinding) becomes same-origin with the agent in the browser's eyes —
/// but its requests still carry its own name in <c>Host</c>, and are refused here.</item>
/// <item><b>Origin</b> must be a Paz page (<see cref="OriginPolicy"/>). The web app always reads
/// pages with <c>fetch</c>, which sends it; an <c>&lt;img&gt;</c> on some other site does not, and
/// gets nothing.</item>
/// </list>
///
/// <para>Only <c>GET /</c> is open, so a person can type the address into a browser to see that the
/// agent is running. It returns nothing else.</para>
/// </summary>
public sealed class AgentGuard(RequestDelegate next, AgentOptions options, ILogger<AgentGuard> logger)
{
    private readonly OriginPolicy _origins = new(options.EffectiveAllowedOrigins);
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        $"127.0.0.1:{options.Port}",
        $"localhost:{options.Port}",
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var request = context.Request;
        var response = context.Response;
        var origin = request.Headers.Origin.ToString();

        if (!_hosts.Contains(request.Host.Value ?? ""))
        {
            await Refuse(context, StatusCodes.Status421MisdirectedRequest, "HOST_NOT_ALLOWED", "Wrong host.");
            Log(context, started, origin, "refused: wrong host");
            return;
        }

        if (request.Path == "/" && HttpMethods.IsGet(request.Method))
        {
            await next(context);
            Log(context, started, origin, null);
            return;
        }

        if (!_origins.IsAllowed(origin))
        {
            await Refuse(context, StatusCodes.Status403Forbidden, "ORIGIN_NOT_ALLOWED", "This page may not use the scanner.");
            Log(context, started, origin, "refused: origin not allowed");
            return;
        }

        response.Headers.AccessControlAllowOrigin = origin;
        response.Headers.Vary = "Origin";

        if (HttpMethods.IsOptions(request.Method))
        {
            response.Headers.AccessControlAllowMethods = "GET, POST, DELETE";
            response.Headers.AccessControlAllowHeaders = request.Headers.AccessControlRequestHeaders.Count > 0
                ? request.Headers.AccessControlRequestHeaders.ToString()
                : "content-type";
            response.Headers.AccessControlMaxAge = "600";
            if (request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
                response.Headers["Access-Control-Allow-Private-Network"] = "true";
            response.StatusCode = StatusCodes.Status204NoContent;
            Log(context, started, origin, null);
            return;
        }

        try
        {
            await next(context);
        }
        catch (Exception error)
        {
            logger.LogError(error, "HTTP {Method} {Path} failed after {Elapsed:0.0} ms",
                request.Method, request.Path + request.QueryString, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }

        Log(context, started, origin, null);
    }

    private void Log(HttpContext context, long started, string origin, string? note)
    {
        var request = context.Request;
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var path = request.Path + request.QueryString;
        var from = string.IsNullOrWhiteSpace(origin) ? "-" : origin;
        if (note is null)
        {
            logger.LogInformation("HTTP {Method} {Path} -> {Status} in {Elapsed:0.0} ms, origin {Origin}",
                request.Method, path, context.Response.StatusCode, elapsed, from);
        }
        else
        {
            logger.LogWarning("HTTP {Method} {Path} -> {Status} in {Elapsed:0.0} ms, origin {Origin}: {Note}",
                request.Method, path, context.Response.StatusCode, elapsed, from, note);
        }
    }

    private static Task Refuse(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ScanErrorInfo(code, message));
    }
}
