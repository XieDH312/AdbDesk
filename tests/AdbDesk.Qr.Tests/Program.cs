using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdbDesk.App;
using ZXing;

// No windows, camera, ADB process, or real pairing credentials are used here.
var samples = new[]
{
    "WIFI:T:ADB;S:studio-test123456;P:TestSecret1234567890;;",
    "WIFI:T:ADB;S:studio-0123456789abcdef0123456789abcdef;P:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef;;"
};
try
{
    foreach (var payload in samples)
    {
        using var stream = new MemoryStream(PairingQrRenderer.RenderPng(payload));
        var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        VerifyDecode(bitmap, payload);
        // Confirm a typical dialog-size rasterization remains machine-readable.
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(260.0 / bitmap.PixelWidth, 260.0 / bitmap.PixelHeight));
        VerifyDecode(scaled, payload);
    }
    Console.WriteLine("PASS real pairing QR PNGs decode to the exact payload at native and dialog size");
    Console.WriteLine("RESULT 1/1 passed (4 image decodes)");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL QR round-trip: {exception.Message}");
    Environment.ExitCode = 1;
}

static void VerifyDecode(BitmapSource image, string expected)
{
    var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
    var stride = converted.PixelWidth * 4;
    var pixels = new byte[stride * converted.PixelHeight];
    converted.CopyPixels(pixels, stride, 0);
    var source = new RGBLuminanceSource(pixels, converted.PixelWidth, converted.PixelHeight, RGBLuminanceSource.BitmapFormat.BGRA32);
    var result = new BarcodeReaderGeneric().Decode(source);
    if (result?.Text != expected) throw new InvalidOperationException("Generated QR could not be decoded to the expected pairing payload.");
}
