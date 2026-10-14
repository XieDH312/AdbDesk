namespace AdbDesk.Core;

public enum DeviceTransport { Unknown, Usb, Network, Emulator }

public record DeviceInfo(string Serial, string Model, string State)
{
    public bool IsReady => State == "device";
    public bool IsNetworkConnection => DeviceDetailsParser.ClassifyTransport(Serial, string.Empty) == DeviceTransport.Network;
    public string DeviceName { get; init; } = "";
    public string AndroidVersion { get; init; } = "";
    public DeviceTransport Transport { get; init; } = DeviceTransport.Unknown;
    public long? TransportId { get; init; }
    public int? WirelessPort { get; init; }
    public string DetailsError { get; init; } = "";
    public DeviceNetworkInfo Network { get; init; } = DeviceNetworkInfo.Empty;
}

public record WirelessResult(bool ModeEnabled, bool Connected, string Endpoint, string Message)
{
    public bool Succeeded { get; init; } = true;
}

public record MirrorOptions(int MaxSize = 1920, int MaxFps = 60, bool Audio = false,
    bool FullScreen = false, MirrorCrop? Crop = null, int? WindowX = null, int? WindowY = null,
    int? WindowWidth = null, int? WindowHeight = null);

public record DisconnectResult(bool Disconnected, string Message);

public record MirrorEvent(string Serial, string Message, bool IsRunning);

public record InstallUpdate(Guid Id, string Serial, string ApkPath, string Status, string Detail);
