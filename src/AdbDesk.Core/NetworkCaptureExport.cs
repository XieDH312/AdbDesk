using System.Text;
using System.Text.Json;

namespace AdbDesk.Core;

/// <summary>Produces local files and command text only; never executes requests.</summary>
public static class NetworkCaptureExport
{
    public static string CreatePowerShellCurl(NetworkCaptureEntry entry, string directory)
    {
        if (!NetworkCaptureProtocol.IsValid(entry)) throw new InvalidDataException("Capture metadata is invalid or exceeds the supported limits.");
        if (!entry.RequestTargetComplete) throw new InvalidOperationException("请求方法或 URL 已截断，无法生成可复现的 cURL。");
        if (!entry.RequestHeadersComplete || entry.RequestHeaderItems is null)
            throw new InvalidOperationException("完整请求头未保存，无法生成可复现的 cURL。");
        if (!IsToken(entry.Method)) throw new ArgumentException("HTTP method is invalid.");
        if (entry.Url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\') ||
            !Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.Host.Length == 0 || uri.UserInfo.Length > 0)
            throw new ArgumentException("Only an absolute HTTP(S) URL without credentials is supported.");
        var body = entry.RequestCapture;
        if (body is null || !body.Complete || body.TotalBytes < 0 || body.SavedBytes != body.TotalBytes || body.Error.Length != 0)
            throw new InvalidOperationException("完整请求正文未保存；不能将缺失正文当作空正文。");
        var isHead = entry.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        if (isHead && body.TotalBytes > 0) throw new InvalidOperationException("无法安全复现带正文的 HEAD 请求。");
        foreach (var header in entry.RequestHeaderItems)
            if (!IsToken(header.Name) || header.Value.Any(char.IsControl)) throw new ArgumentException("Unsafe HTTP header.");
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Connection", "Proxy-Connection", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Content-Length", "Proxy-Authorization", "Proxy-Authenticate" };
        foreach (var connection in entry.RequestHeaderItems.Where(h => h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase)))
            foreach (var token in connection.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!IsToken(token)) throw new ArgumentException("Invalid Connection header token.");
                excluded.Add(token);
            }
        // Content-Encoding belongs to the raw file. Removing it would change body semantics.
        if (body.TotalBytes > 0 && excluded.Contains("Content-Encoding") && entry.RequestHeaderItems.Any(h => h.Name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Connection marks Content-Encoding as hop-by-hop; cannot safely reproduce this request.");
        var command = new StringBuilder("curl.exe --disable --globoff --path-as-is --proto '=http,https' --request ").Append(Quote(entry.Method)).Append(" --url ").Append(Quote(entry.Url));
        if (isHead) command.Append(" --head");
        var included = entry.RequestHeaderItems.Where(h => !excluded.Contains(h.Name)).ToArray();
        foreach (var header in included)
            command.Append(" --header ").Append(Quote(header.Value.Length == 0 ? header.Name + ";" : header.Name + ": " + header.Value));
        if (!included.Any(h => h.Name.Equals("Accept", StringComparison.OrdinalIgnoreCase))) command.Append(" --header 'Accept:'");
        if (body.TotalBytes > 0)
        {
            // Read validates the actual size and path, not only the untrusted metadata.
            var bytes = NetworkCaptureHistory.ReadBody(directory, body);
            if (bytes.LongLength != body.TotalBytes) throw new InvalidDataException("Saved body length does not match metadata.");
            var path = NetworkCaptureHistory.GetBodyPath(directory, body);
            if (!included.Any(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))) command.Append(" --header 'Content-Type:'");
            command.Append(" --data-binary ").Append(Quote("@" + path));
            return "# PowerShell 7. Depends on the original local saved body file; keep it in place.\n" + command;
        }
        if (body.FileName is not null && NetworkCaptureHistory.ReadBody(directory, body).Length != 0) throw new InvalidDataException("Empty body metadata does not match file.");
        return "# PowerShell 7. Generated command only; review before running.\n" + command;
    }

    private static bool IsToken(string value) => value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));
    private static string Quote(string text)
    {
        // PowerShell also recognizes typographic quote characters. Reject these ambiguous literals.
        if (text.Any(c => char.IsControl(c) || c is '\u2018' or '\u2019' or '\u201c' or '\u201d')) throw new ArgumentException("Unsupported control or typographic quote in command argument.");
        return "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    public static Task WriteHarAsync(string fileName, IReadOnlyList<NetworkCaptureEntry> entries, string directory, CancellationToken token = default) =>
        WriteHarAsync(fileName, (IEnumerable<NetworkCaptureEntry>)entries, directory, token);

    /// <summary>Enumerates one entry at a time and atomically replaces the destination after successful completion.</summary>
    public static async Task WriteHarAsync(string fileName, IEnumerable<NetworkCaptureEntry> entries, string directory, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var destination = Path.GetFullPath(fileName);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            token.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject(); writer.WritePropertyName("log"); writer.WriteStartObject();
                writer.WriteString("version", "1.2"); writer.WritePropertyName("creator"); writer.WriteStartObject(); writer.WriteString("name", "AdbDesk"); writer.WriteString("version", "1"); writer.WriteEndObject();
                writer.WriteString("comment", "Raw capture exported locally. Unknown timing and byte counts are -1. Cookie headers are retained verbatim; cookie arrays are not parsed. Extensions describe unavailable or incomplete data.");
                writer.WritePropertyName("entries"); writer.WriteStartArray();
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    WriteEntry(writer, entry, directory);
                    await writer.FlushAsync(token).ConfigureAwait(false);
                }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject();
                await writer.FlushAsync(token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteEntry(Utf8JsonWriter w, NetworkCaptureEntry e, string directory)
    {
        if (!NetworkCaptureProtocol.IsValid(e)) throw new InvalidDataException("Capture metadata is invalid; HAR export was not completed.");
        w.WriteStartObject(); w.WriteString("startedDateTime", e.Time); w.WriteNumber("time", e.Finished && e.DurationMs is { } ms && double.IsFinite(ms) && ms >= 0 ? ms : -1);
        w.WriteBoolean("_finished", e.Finished); if (e.Error.Length > 0) w.WriteString("_error", e.Error);
        w.WritePropertyName("request"); w.WriteStartObject(); w.WriteString("method", e.Method); w.WriteString("url", e.Url); w.WriteString("httpVersion", e.HttpVersion);
        w.WriteBoolean("_requestTargetComplete", e.RequestTargetComplete);
        if (!e.RequestTargetComplete) w.WriteString("_requestTargetComment", "The captured request method or URL was truncated; method, url and queryString may be incomplete.");
        WriteHeaders(w, e.RequestHeaderItems, e.RequestHeadersComplete);
        w.WritePropertyName("queryString"); w.WriteStartArray();
        if (Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) && uri.Query.Length > 1)
            foreach (var part in uri.Query[1..].Split('&'))
            {
                var pair = part.Split('=', 2);
                w.WriteStartObject(); w.WriteString("name", QueryDecode(pair[0])); w.WriteString("value", pair.Length == 2 ? QueryDecode(pair[1]) : ""); w.WriteEndObject();
            }
        w.WriteEndArray(); w.WriteNumber("headersSize", -1); w.WriteNumber("bodySize", BodySize(e.RequestCapture));
        WriteBody(w, e.RequestCapture, e.RequestHeaderItems, e.RequestHeadersComplete, directory, response: false);
        w.WriteEndObject();
        w.WritePropertyName("response"); w.WriteStartObject(); w.WriteNumber("status", e.StatusCode ?? 0); w.WriteString("statusText", ""); w.WriteString("httpVersion", e.ResponseHttpVersion);
        WriteHeaders(w, e.ResponseHeaderItems, e.ResponseHeadersComplete);
        w.WriteString("redirectURL", NetworkCaptureView.Header(e.ResponseHeaderItems, "Location")); w.WriteNumber("headersSize", -1); w.WriteNumber("bodySize", BodySize(e.ResponseCapture));
        WriteBody(w, e.ResponseCapture, e.ResponseHeaderItems, e.ResponseHeadersComplete, directory, response: true);
        w.WriteEndObject(); w.WritePropertyName("cache"); w.WriteStartObject(); w.WriteEndObject();
        w.WritePropertyName("timings"); w.WriteStartObject();
        foreach (var name in new[] { "blocked", "dns", "connect", "send", "wait", "receive", "ssl" }) w.WriteNumber(name, -1);
        w.WriteEndObject(); w.WriteEndObject();
    }

    private static long BodySize(CapturedBody? body) => body is { TotalBytes: >= 0, Complete: true } ? body.TotalBytes : -1;
    private static string QueryDecode(string text) { try { return Uri.UnescapeDataString(text.Replace('+', ' ')); } catch (UriFormatException) { return text; } }
    private static void WriteHeaders(Utf8JsonWriter w, IReadOnlyList<NetworkHeader>? headers, bool complete)
    {
        w.WritePropertyName("headers"); w.WriteStartArray();
        foreach (var header in headers ?? []) { w.WriteStartObject(); w.WriteString("name", header.Name); w.WriteString("value", header.Value); w.WriteEndObject(); }
        w.WriteEndArray(); w.WriteBoolean("_headersComplete", complete && headers is not null);
        w.WritePropertyName("cookies"); w.WriteStartArray(); w.WriteEndArray();
    }

    private static void WriteBody(Utf8JsonWriter w, CapturedBody? body, IReadOnlyList<NetworkHeader>? headers, bool headersComplete, string directory, bool response)
    {
        var decoded = NetworkCaptureView.Decode(directory, body, headers, headersComplete);
        w.WritePropertyName("_capture");
        if (body is null) w.WriteNullValue(); else JsonSerializer.Serialize(w, body);
        if (!response && decoded.Bytes is null) { w.WriteString("comment", decoded.Comment); return; }
        if (!response && decoded.Bytes!.Length == 0) return;
        w.WritePropertyName(response ? "content" : "postData"); w.WriteStartObject();
        var mime = NetworkCaptureView.Header(headers, "Content-Type");
        w.WriteString("mimeType", mime);
        if (response) w.WriteNumber("size", decoded.Bytes?.LongLength ?? -1);
        if (decoded.Bytes is { } bytes)
        {
            if (response && body is not null && NetworkCaptureView.Header(headers, "Content-Encoding").Length > 0) w.WriteNumber("compression", bytes.LongLength - body.TotalBytes);
            if (NetworkCaptureView.TryText(bytes, mime, out var text)) w.WriteString("text", text);
            else
            {
                w.WriteString("text", Convert.ToBase64String(bytes));
                w.WriteString(response ? "encoding" : "_encoding", "base64");
                if (!response) w.WriteString("comment", "Binary request content is base64 in the _encoding extension; HAR 1.2 has no standard binary postData encoding.");
            }
            if (!response && NetworkCaptureView.Header(headers, "Content-Encoding").Length > 0) w.WriteString("_contentDecoded", "Content-Encoding was decoded; original headers describe the raw captured request.");
        }
        else w.WriteString("comment", decoded.Comment);
        w.WriteEndObject();
    }
}
