namespace AdbDesk.Core;

public enum DeviceTransport { Unknown, Usb, Network, Emulator }

public record DeviceInfo(string Serial, string Model, string State)
{
    public bool IsReady => State == "device";
    public bool IsNetworkConnection => DeviceDetailsParser.ClassifyTransport(Serial, string.Empty) == DeviceTransport.Network;
    public string DeviceName { get; init; } = "";
    public string AndroidVersion { get; init; } = "";
    public DeviceTransport Transport { get; init; } = DeviceTransport.Unknown;
    public int? WirelessPort { get; init; }
    public string DetailsError { get; init; } = "";
}

public record WirelessResult(bool ModeEnabled, bool Connected, string Endpoint, string Message)
{
    public bool Succeeded { get; init; } = true;
}

public record MirrorOptions(int MaxSize = 1920, int MaxFps = 60, bool Audio = false);

public record DisconnectResult(bool Disconnected, string Message);

public record MirrorEvent(string Serial, string Message, bool IsRunning);

public record InstallUpdate(Guid Id, string Serial, string ApkPath, string Status, string Detail);
