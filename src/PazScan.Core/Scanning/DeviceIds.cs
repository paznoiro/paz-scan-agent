using System.Text;

namespace PazScan.Core.Scanning;

/// <summary>
/// The id the web app holds for a scanner: the driver and the driver's own id, URL-safe.
///
/// <para>Derived rather than handed out from a table, so the id a browser remembered from yesterday
/// still names the same USB scanner after the agent restarts.</para>
/// </summary>
public static class DeviceIds
{
    public static string Encode(string driver, string nativeId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{driver}|{nativeId}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static bool TryDecode(string id, out string driver, out string nativeId)
    {
        driver = "";
        nativeId = "";
        try
        {
            var base64 = id.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var bar = text.IndexOf('|');
            if (bar <= 0 || bar == text.Length - 1) return false;
            driver = text[..bar];
            nativeId = text[(bar + 1)..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
