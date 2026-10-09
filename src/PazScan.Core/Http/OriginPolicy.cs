namespace PazScan.Core.Http;

/// <summary>
/// Which web pages may drive the scanner.
///
/// <para>The agent listens on the loopback address, so anything running in the user's browser can
/// reach it. A browser always sends <c>Origin</c> on a cross-origin request and a page cannot forge
/// it, so this list is what keeps an unrelated website from starting a scan or reading the pages.</para>
/// </summary>
public sealed class OriginPolicy
{
    private readonly IReadOnlyList<Pattern> _patterns;

    public OriginPolicy(IEnumerable<string> patterns)
    {
        _patterns = patterns.Select(Pattern.Parse).ToList();
    }

    public bool IsAllowed(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        // A sandboxed frame or a file:// page sends the literal "null".
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        // An origin is scheme, host and port. A path, query or user part means it is not one.
        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.UserInfo.Length > 0) return false;
        return _patterns.Any(pattern => pattern.Matches(uri));
    }

    private sealed record Pattern(string Scheme, string Host, bool AnySubdomain, int? Port, bool AnyPort)
    {
        public static Pattern Parse(string text)
        {
            var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0) throw new FormatException($"Allowed origin '{text}' has no scheme.");
            var scheme = text[..schemeEnd].ToLowerInvariant();
            var rest = text[(schemeEnd + 3)..].TrimEnd('/');

            int? port = null;
            var anyPort = false;
            var colon = rest.LastIndexOf(':');
            if (colon >= 0)
            {
                var portText = rest[(colon + 1)..];
                if (portText == "*") anyPort = true;
                else if (int.TryParse(portText, out var parsed)) port = parsed;
                else throw new FormatException($"Allowed origin '{text}' has a bad port.");
                rest = rest[..colon];
            }

            var anySubdomain = rest.StartsWith("*.", StringComparison.Ordinal);
            var host = (anySubdomain ? rest[2..] : rest).ToLowerInvariant();
            if (host.Length == 0 || host.Contains('*'))
                throw new FormatException($"Allowed origin '{text}' may only use '*.' at the start of the host.");
            return new Pattern(scheme, host, anySubdomain, port, anyPort);
        }

        public bool Matches(Uri origin)
        {
            if (!string.Equals(origin.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return false;

            var host = origin.Host.ToLowerInvariant();
            var hostMatches = AnySubdomain
                ? host.EndsWith("." + Host, StringComparison.Ordinal) && host.Length > Host.Length + 1
                : host == Host;
            if (!hostMatches) return false;

            if (AnyPort) return true;
            return Port is { } port ? origin.Port == port : origin.IsDefaultPort;
        }
    }
}
