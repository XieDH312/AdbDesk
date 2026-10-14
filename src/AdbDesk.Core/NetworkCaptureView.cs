using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AdbDesk.Core;

public static class NetworkCaptureView
{
    internal const int MaximumDecodedBytes = 4 * 1024 * 1024;
    /// <summary>Domain matches the exact host or a subdomain at a DNS label boundary.</summary>
    public static bool Matches(NetworkCaptureEntry entry, string keyword, string domain, string method, string status,
        string contentType, bool onlyFailures, double? slowMs)
    {
        static bool Contains(string value, string part) => value.Contains(part, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(keyword) && !Contains(entry.Url, keyword) && !Contains(entry.Method, keyword) && !Contains(entry.Error, keyword) && !Contains(entry.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", keyword)) return false;
        if (!string.IsNullOrWhiteSpace(domain))
        {
            if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri)) return false;
            var host = uri.IdnHost.TrimEnd('.'); var filter = domain.Trim().TrimEnd('.');
            if (!host.Equals(filter, StringComparison.OrdinalIgnoreCase) && !host.EndsWith("." + filter, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (method.Length > 0 && !entry.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) return false;
        if (status.Length > 0 && (status.Length != 3 || !status.EndsWith("xx", StringComparison.OrdinalIgnoreCase) || entry.StatusCode is not { } code || code / 100 != status[0] - '0')) return false;
        if (contentType.Length > 0 && !Contains(Header(entry.ResponseHeaderItems, "Content-Type"), contentType)) return false;
        if (onlyFailures && !(entry.StatusCode >= 400 || entry.Error.Length > 0)) return false;
        return slowMs is null || (entry.DurationMs is { } duration && duration >= slowMs);
    }

    public static string ReadBodyText(NetworkCaptureEntry entry, bool response, string directory)
    {
        var body = response ? entry.ResponseCapture : entry.RequestCapture;
        var headers = response ? entry.ResponseHeaderItems : entry.RequestHeaderItems;
        var decoded = Decode(directory, body, headers, response ? entry.ResponseHeadersComplete : entry.RequestHeadersComplete);
        if (decoded.Bytes is null)
            return "[正文不可完整读取 / body unavailable: " + decoded.Comment + "]\n[仅预览 / preview only]\n" + (response ? entry.ResponseBody : entry.RequestBody);
        if (TryText(decoded.Bytes, Header(headers, "Content-Type"), out var text)) return text;
        return "[二进制正文 / binary body; base64]\n" + Convert.ToBase64String(decoded.Bytes);
    }

    public static string FormatJson(string text)
    {
        try { using var doc = JsonDocument.Parse(text); return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }); }
        catch (JsonException) { return text; }
    }

    internal static string Header(IReadOnlyList<NetworkHeader>? headers, string name) =>
        headers?.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";

    internal sealed record DecodedBody(byte[]? Bytes, string Comment);
    internal static DecodedBody Decode(string directory, CapturedBody? body, IReadOnlyList<NetworkHeader>? headers, bool headersComplete)
    {
        if (body is null) return new(null, "Body metadata was not captured.");
        if (!body.Complete || body.TotalBytes < 0 || body.SavedBytes != body.TotalBytes || body.Error.Length > 0)
            return new(null, $"Incomplete capture: saved {body.SavedBytes} of {body.TotalBytes} bytes. {body.Error}");
        if (!headersComplete || headers is null) return new(null, "Headers are incomplete; body content encoding cannot be determined reliably.");
        try
        {
            if (body.TotalBytes == 0 && body.FileName is null) return new([], "");
            var bytes = NetworkCaptureHistory.ReadBody(directory, body);
            if (bytes.LongLength != body.TotalBytes) throw new InvalidDataException("Saved body length does not match metadata.");
            var encodings = (headers ?? []).Where(h => h.Name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
                .SelectMany(h => h.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
            if (encodings.Length > 8) throw new InvalidDataException("Too many content encodings.");
            foreach (var encoding in encodings.Reverse())
            {
                if (encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)) continue;
                using var input = new MemoryStream(bytes, false);
                using Stream decoder = encoding.ToLowerInvariant() switch
                {
                    "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
                    "deflate" => new ZLibStream(input, CompressionMode.Decompress),
                    "br" => new BrotliStream(input, CompressionMode.Decompress),
                    _ => throw new InvalidDataException("Unsupported Content-Encoding: " + encoding)
                };
                using var output = new MemoryStream();
                var buffer = new byte[8192]; int read;
                while ((read = decoder.Read(buffer, 0, Math.Min(buffer.Length, MaximumDecodedBytes + 1 - (int)output.Length))) != 0)
                {
                    if (output.Length + read > MaximumDecodedBytes) throw new InvalidDataException("Decoded body exceeds the 4 MiB limit.");
                    output.Write(buffer, 0, read);
                }
                bytes = output.ToArray();
            }
            return new(bytes, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(null, ex.Message); }
    }

    internal static bool TryText(byte[] bytes, string contentType, out string text)
    {
        text = "";
        if (bytes.Length == 0) return true;
        var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (!(mime.StartsWith("text/", StringComparison.Ordinal) || mime.Contains("json") || mime.Contains("xml") || mime is "application/javascript" or "application/x-www-form-urlencoded" or "application/graphql")) return false;
        try
        {
            var charset = contentType.Split(';').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("charset=", StringComparison.OrdinalIgnoreCase));
            var encoding = charset is null ? new UTF8Encoding(false, true) : Encoding.GetEncoding(charset[8..].Trim(' ', '\'', '"'), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            text = encoding.GetString(bytes);
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
