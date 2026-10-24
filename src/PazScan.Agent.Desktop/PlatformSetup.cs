using Microsoft.Extensions.Logging;
using NAPS2.Images.ImageSharp;
using NAPS2.Scan;

namespace PazScan.Agent.Desktop;

/// <summary>
/// The parts of NAPS2 that differ by platform.
///
/// <para>macOS and Linux both use SANE for USB scanners plus NAPS2's own eSCL driver for network
/// scanners. macOS gets SANE from <c>NAPS2.Sane.Binaries</c>; Linux uses the system SANE libraries.
/// The Apple ImageCaptureCore driver is not in the NAPS2 NuGet build, so it is not listed here.</para>
/// </summary>
internal static class PlatformSetup
{
    public static IReadOnlyList<Driver> Drivers { get; } = [Driver.Sane, Driver.Escl];

    public static ScanningContext CreateScanningContext(ILogger logger)
    {
        // ImageSharp is managed-only, so this works on both macOS and Linux without GDI or GTK.
        var scanning = new ScanningContext(new ImageSharpImageContext())
        {
            Logger = logger,
        };
        return scanning;
    }
}
