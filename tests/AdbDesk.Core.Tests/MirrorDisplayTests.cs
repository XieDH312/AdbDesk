using System.Buffers.Binary;
using AdbDesk.Core;

internal static class MirrorDisplayTests
{
    // Catches cropping in current landscape coordinates instead of scrcpy's natural coordinates.
    internal static Task GeometryAsync()
    {
        var natural = MirrorDisplayGeometry.ParseNaturalSize("Physical size: 1440x3200\nOverride size: 1080x2400\n");
        Equal(new MirrorDisplaySize(1080, 2400), natural, "active override takes precedence");
        var landscape = MirrorDisplayGeometry.ReadScreenshotSize(Header(2400, 1080));
        Equal(new MirrorCrop(1080, 1920, 0, 240), MirrorDisplayGeometry.CropToFit(natural, landscape, 3840, 2160), "landscape phone crops horizontal video edges in natural coordinates");
        Equal(new MirrorCrop(1080, 606, 0, 897), MirrorDisplayGeometry.CropToFit(natural, natural, 1920, 1080), "portrait crop remains centered and preserves monitor ratio within pixel rounding");
        Equal(new MirrorCrop(2560, 1440, 0, 80), MirrorDisplayGeometry.CropToFit(new(2560, 1600), new(2560, 1600), 1920, 1080), "landscape tablet keeps natural coordinates");
        Equal(new MirrorCrop(1920, 1080, 0, 0), MirrorDisplayGeometry.CropToFit(new(1920, 1080), new(1920, 1080), 3840, 2160), "matching aspect ratio keeps the entire screen");
        Equal("1080:1920:0:240", new MirrorCrop(1080, 1920, 0, 240).ToScrcpyArgument(), "crop argument order is width height x y");
        Throws<InvalidDataException>(() => MirrorDisplayGeometry.ParseNaturalSize("Physical size: 0x2400"));
        Throws<InvalidDataException>(() => MirrorDisplayGeometry.ReadScreenshotSize([137, 80, 78, 71]));
        Throws<InvalidDataException>(() => MirrorDisplayGeometry.CropToFit(natural, new(1200, 2500), 1920, 1080));
        Throws<ArgumentOutOfRangeException>(() => MirrorDisplayGeometry.CropToFit(natural, landscape, 0, 1080));
        Throws<InvalidDataException>(() => MirrorDisplayGeometry.CropToFit(new(1000, 1000), new(1000, 1000), 1920, 1080));
        Equal(new MirrorCrop(1000, 1000, 0, 0), MirrorDisplayGeometry.CropToFit(new(1000, 1000), new(1000, 1000), 1000, 1000), "square output needs no rotation inference");
        return Task.CompletedTask;
    }

    // Catches losing device targeting/fullscreen/crop, or invalid bounds reaching the process launcher.
    internal static async Task ArgumentsAsync()
    {
        using var fixture = new ToolFixture();
        using var service = new MirrorService(fixture.Paths);
        var device = new DeviceInfo("display-phone", "Test phone", "device");
        service.Start(device, new MirrorOptions(FullScreen: true, Crop: new(1080, 1920, 0, 240), WindowX: -1700, WindowY: 100, WindowWidth: 768, WindowHeight: 432));
        await TestWait.UntilAsync(() => fixture.CountStarts("mirror", device.Serial) == 1, TimeSpan.FromSeconds(3));
        var invocation = await fixture.ReadInvocationAsync("scrcpy");
        Equal("--serial|display-phone|--window-title|Test phone (display-phone)|--max-size|1920|--max-fps|60|--no-audio|--fullscreen|--crop|1080:1920:0:240|--window-x|-1700|--window-y|100|--window-width|768|--window-height|432", invocation, "native arguments preserve crop and monitor positioning");
        service.Stop(device.Serial);
        Throws<ArgumentOutOfRangeException>(() => service.Start(device, new MirrorOptions(Crop: new(0, 1920, 0, 240))));
        Throws<ArgumentOutOfRangeException>(() => service.Start(device, new MirrorOptions(WindowWidth: 0)));
        fixture.SetResponse(device.Serial, "display-size", "Physical size: 1080x2400\n");
        Equal(new MirrorDisplaySize(1080, 2400), await new AdbService(fixture.Paths).GetDisplaySizeAsync(device), "geometry query targets the selected phone");
        Equal(true, fixture.HasInvocation("-s", device.Serial, "shell", "wm size"), "size query is read-only and device scoped");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await new AdbService(fixture.Paths).GetDisplaySizeAsync(device, canceled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
    }

    private static byte[] Header(uint width, uint height)
    {
        var bytes = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }
    private static void Equal<T>(T expected, T actual, string message) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: {expected} != {actual}"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
}
