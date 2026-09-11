namespace AdbDesk.Core;

public record DeviceInfo(string Serial, string Model, string State)
{
    public bool IsReady => State == "device";
}

public record MirrorOptions(int MaxSize = 1280, int MaxFps = 30, bool Audio = false);

public record MirrorEvent(string Serial, string Message, bool IsRunning);

public record InstallUpdate(Guid Id, string Serial, string ApkPath, string Status, string Detail);
