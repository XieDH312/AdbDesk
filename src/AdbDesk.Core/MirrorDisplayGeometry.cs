using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AdbDesk.Core;

public sealed record MirrorDisplaySize(int Width, int Height);

public sealed record MirrorCrop(int Width, int Height, int X, int Y)
{
    internal void Validate()
    {
        if (Width is < 2 or > 32768 || Height is < 2 or > 32768 || X < 0 || Y < 0 ||
            (long)X + Width > 32768 || (long)Y + Height > 32768)
            throw new ArgumentOutOfRangeException(nameof(MirrorCrop), "裁剪区域无效。");
    }
    public string ToScrcpyArgument()
    {
        Validate();
        return FormattableString.Invariant($"{Width}:{Height}:{X}:{Y}");
    }
}

public static partial class MirrorDisplayGeometry
{
    public static MirrorCrop? CreateCrop(MirrorDisplaySize natural, MirrorDisplaySize current,
        int targetWidth, int targetHeight, MirrorDisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (settings.Mode == MirrorScaleMode.Complete) return null;
        ArgumentNullException.ThrowIfNull(natural);
        ArgumentNullException.ThrowIfNull(current);
        if (!ValidSize(targetWidth, targetHeight)) throw new ArgumentOutOfRangeException(nameof(targetWidth));
        if (!ValidSize(natural.Width, natural.Height) || !ValidSize(current.Width, current.Height))
            throw new InvalidDataException("手机屏幕尺寸无效。");
        var region = settings.EffectiveRegion;
        if (region != MirrorCropRegion.Full && settings.SourceSize != current)
            throw new InvalidDataException("手机方向或分辨率已变化，请打开显示设置重新选择裁剪区域。");
        if (natural.Width == natural.Height && (targetWidth != targetHeight || region != MirrorCropRegion.Full))
            throw new InvalidDataException("方形手机屏幕无法仅凭尺寸判断旋转方向，请选择完整显示。");
        var transposed = natural != current && natural.Width == current.Height && natural.Height == current.Width;
        if (natural != current && !transposed) throw new InvalidDataException("手机分辨率在读取期间发生变化，请重试。");
        var x = (int)Math.Floor(current.Width * region.Left);
        var y = (int)Math.Floor(current.Height * region.Top);
        var width = (int)Math.Floor(current.Width * region.Right) - x;
        var height = (int)Math.Floor(current.Height * region.Bottom) - y;
        var wide = (long)width * targetHeight > (long)height * targetWidth;
        if (wide && settings.Mode is MirrorScaleMode.Fill or MirrorScaleMode.FillHeight)
        {
            var reduced = (int)((long)height * targetWidth / targetHeight);
            reduced -= reduced % 2;
            x += (width - reduced) / 2;
            width = reduced;
        }
        else if (!wide && settings.Mode is MirrorScaleMode.Fill or MirrorScaleMode.FillWidth)
        {
            var reduced = (int)((long)width * targetHeight / targetWidth);
            reduced -= reduced % 2;
            y += (height - reduced) / 2;
            height = reduced;
        }
        width -= width % 2;
        height -= height % 2;
        if (width < 2 || height < 2) throw new InvalidDataException("裁剪区域过小，请扩大选区。");
        // scrcpy v4.1 VideoFilter.addCrop transposes all four edges for odd rotations.
        // It does not rotate/mirror the rectangle around the natural screen bounds.
        return transposed ? new(height, width, y, x) : new(width, height, x, y);
    }

    public static MirrorDisplaySize ParseNaturalSize(string output)
    {
        var matches = SizeRegex().Matches(output ?? "");
        var selected = matches.Cast<Match>().LastOrDefault(match => match.Groups[1].Value == "Override") ??
                       matches.Cast<Match>().LastOrDefault(match => match.Groups[1].Value == "Physical");
        if (selected is null || !int.TryParse(selected.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(selected.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
            !ValidSize(width, height)) throw new InvalidDataException("无法读取手机当前分辨率，请选择完整显示或重新连接后重试。");
        return new(width, height);
    }

    public static MirrorDisplaySize ReadScreenshotSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !png[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(png[8..12]) != 13 || !png[12..16].SequenceEqual("IHDR"u8))
            throw new InvalidDataException("无法读取截图尺寸，未启动裁剪投屏。");
        var width = BinaryPrimitives.ReadUInt32BigEndian(png[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(png[20..24]);
        if (width is < 2 or > 32768 || height is < 2 or > 32768) throw new InvalidDataException("截图尺寸无效。");
        return new((int)width, (int)height);
    }

    public static MirrorCrop CropToFit(MirrorDisplaySize natural, MirrorDisplaySize current, int targetWidth, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(natural);
        ArgumentNullException.ThrowIfNull(current);
        if (!ValidSize(targetWidth, targetHeight)) throw new ArgumentOutOfRangeException(nameof(targetWidth), "电脑屏幕尺寸无效。");
        if (!ValidSize(natural.Width, natural.Height) || !ValidSize(current.Width, current.Height)) throw new InvalidDataException("手机屏幕尺寸无效。");
        if (natural.Width == natural.Height && targetWidth != targetHeight)
            throw new InvalidDataException("方形手机屏幕无法仅凭尺寸判断旋转方向，请选择完整显示。");
        var same = natural == current;
        var transposed = !same && natural.Width == current.Height && natural.Height == current.Width;
        if (!same && !transposed) throw new InvalidDataException("手机分辨率在读取期间发生变化，请重新启动投屏。");
        var width = current.Width;
        var height = current.Height;
        if ((long)width * targetHeight > (long)height * targetWidth)
            width = (int)((long)height * targetWidth / targetHeight);
        else
            height = (int)((long)width * targetHeight / targetWidth);
        // Encoders require at least even crop dimensions. The subpixel aspect error is bounded.
        width -= width % 2;
        height -= height % 2;
        if (width < 2 || height < 2) throw new InvalidDataException("裁剪区域过小，请选择完整显示。");
        var x = (current.Width - width) / 2;
        var y = (current.Height - height) / 2;
        // scrcpy transposes the natural crop rectangle on 90/270-degree device rotations.
        return transposed ? new(height, width, y, x) : new(width, height, x, y);
    }

    private static bool ValidSize(int width, int height) => width is >= 2 and <= 32768 && height is >= 2 and <= 32768;
    [GeneratedRegex(@"^\s*(Physical|Override) size:\s*(\d+)x(\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();
}
