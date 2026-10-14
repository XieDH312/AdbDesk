using QRCoder;

namespace AdbDesk.App;

public static class PairingQrRenderer
{
    public static byte[] RenderPng(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        return PngByteQRCodeHelper.GetQRCode(payload, QRCodeGenerator.ECCLevel.Q, size: 8);
    }
}
