using NAPS2.Scan.Exceptions;

namespace PazScan.Core.Scanning;

/// <summary>A failure the person at the scanner can do something about, with its code for the web app.</summary>
public sealed class ScanFailure(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public static class ScanErrors
{
    public static ScanErrorInfo From(Exception error) => error switch
    {
        ScanFailure failure => new(failure.Code, failure.Message),
        DeviceFeederEmptyException => new("FEEDER_EMPTY", "There is no paper in the feeder."),
        DevicePaperJamException => new("PAPER_JAM", "The paper jammed. Clear it and scan the rest."),
        DeviceCoverOpenException => new("COVER_OPEN", "The scanner's cover is open."),
        DeviceWarmingUpException => new("WARMING_UP", "The scanner is warming up. Try again in a moment."),
        DeviceBusyException => new("DEVICE_BUSY", "The scanner is busy with another job."),
        DeviceOfflineException => new("DEVICE_OFFLINE", "The scanner is switched off or disconnected."),
        DeviceNotFoundException => new("DEVICE_NOT_FOUND", "The scanner could not be found. Check it is on and connected."),
        NoFeederSupportException => new("NO_FEEDER", "This scanner has no document feeder. Choose Flatbed."),
        NoDuplexSupportException => new("NO_DUPLEX", "This scanner cannot scan both sides. Choose Feeder."),
        DriverNotSupportedException => new("DRIVER_NOT_SUPPORTED", "This kind of scanner is not supported on this computer."),
        DeviceCommunicationException => new("COMMUNICATION", "The scanner stopped responding."),
        DeviceException device when !string.IsNullOrWhiteSpace(device.Message) => new("DEVICE_ERROR", device.Message),
        _ => new("SCAN_FAILED", string.IsNullOrWhiteSpace(error.Message) ? "The scan failed." : error.Message),
    };
}
