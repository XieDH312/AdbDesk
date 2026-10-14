using System.Text.Json;

namespace AdbDesk.Core;

public sealed record NetworkCaptureEntry(string Id, DateTimeOffset Time, string Method, string Url,
    int? StatusCode, double? DurationMs, string RequestHeaders, string RequestBody,
    string ResponseHeaders, string ResponseBody, string Error)
{
    public IReadOnlyList<NetworkHeader>? RequestHeaderItems { get; init; }
    public IReadOnlyList<NetworkHeader>? ResponseHeaderItems { get; init; }
    public bool RequestHeadersComplete { get; init; }
    public bool RequestTargetComplete { get; init; } = true;
    public bool ResponseHeadersComplete { get; init; }
    public CapturedBody? RequestCapture { get; init; }
    public CapturedBody? ResponseCapture { get; init; }
    public string HttpVersion { get; init; } = "";
    public string ResponseHttpVersion { get; init; } = "";
    public bool Finished { get; init; }
}

public sealed record NetworkHeader(string Name, string Value);
public sealed record CapturedBody(string? FileName, long TotalBytes, long SavedBytes, bool Complete, string Error);
public sealed record NetworkCaptureDiagnostics(string Engine, string Tunnel, string Proxy, string Traffic, string LastError);

internal static class NetworkCaptureProtocol
{
    public const string Prefix = "ADBDESK_CAPTURE ";
    // JSON escapes each non-ASCII character; two bounded 64 KiB previews plus headers.
    public const int MaximumLineLength = 1_100_000;
    public static bool IsReady(string line) => line.Length <= MaximumLineLength && line.StartsWith(Prefix, StringComparison.Ordinal) && GetType(line) == "ready";
    private static string? GetType(string line)
    {
        try { using var json = JsonDocument.Parse(line[Prefix.Length..]); return json.RootElement.GetProperty("type").GetString(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }
    public static bool TryParse(string line, out NetworkCaptureEntry? entry)
    {
        entry = null;
        if (line.Length > MaximumLineLength || !line.StartsWith(Prefix, StringComparison.Ordinal) || GetType(line) != "entry") return false;
        try
        {
            entry = JsonSerializer.Deserialize<NetworkCaptureEntry>(line[Prefix.Length..]);
            if (!IsValid(entry))
            { entry = null; return false; }
            return true;
        }
        catch (JsonException) { return false; }
    }
    internal static bool IsValid(NetworkCaptureEntry? entry) =>
        !(entry is null || string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Length > 128 || entry.Url is null || entry.Url.Length > 16384 ||
                entry.Method is null || entry.Method.Length > 128 || entry.RequestHeaders is null || entry.ResponseHeaders is null || entry.RequestBody is null || entry.ResponseBody is null || entry.Error is null ||
                entry.RequestHeaders.Length > 16_448 || entry.ResponseHeaders.Length > 16_448 || entry.RequestBody.Length > 65_792 || entry.ResponseBody.Length > 65_792 || entry.Error.Length > 4096 ||
                entry.StatusCode is < 100 or > 599 || (entry.DurationMs is { } duration && (!double.IsFinite(duration) || duration < 0)) ||
                entry.HttpVersion is null || entry.HttpVersion.Length > 32 ||
                entry.ResponseHttpVersion is null || entry.ResponseHttpVersion.Length > 32 ||
                !ValidHeaders(entry.RequestHeaderItems) || !ValidHeaders(entry.ResponseHeaderItems) ||
                (entry.RequestHeadersComplete && entry.RequestHeaderItems is null) || (entry.ResponseHeadersComplete && entry.ResponseHeaderItems is null) ||
                !ValidBody(entry.RequestCapture) || !ValidBody(entry.ResponseCapture));

    private static bool ValidHeaders(IReadOnlyList<NetworkHeader>? headers) => headers is null ||
        (headers.Count <= 256 && headers.All(h => h is not null && h.Name is not null && h.Value is not null && h.Name.Length <= 16_384 && h.Value.Length <= 16_384) &&
         headers.Sum(h => (long)h.Name.Length + h.Value.Length) <= 16_384);

    internal static bool ValidBody(CapturedBody? body) => body is null ||
        (body.TotalBytes >= 0 && body.SavedBytes >= 0 && body.SavedBytes <= body.TotalBytes && body.SavedBytes <= NetworkCaptureHistory.MaximumBodyBytes &&
         body.Error is not null && body.Error.Length <= 4096 && (body.FileName is null || IsSafeFileName(body.FileName)) &&
         (body.SavedBytes == 0 || body.FileName is not null) && (!body.Complete || (body.TotalBytes == body.SavedBytes && body.Error.Length == 0)));

    internal static bool IsSafeFileName(string name) => name.Length is > 0 and <= 128 && name is not "." and not ".." &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !name.EndsWith('.') && !Path.IsPathRooted(name);

    public static bool TryDiagnostic(string line, out string error)
    {
        error = "";
        if (line.Length > MaximumLineLength || !line.StartsWith(Prefix, StringComparison.Ordinal) || GetType(line) != "diagnostic") return false;
        try
        {
            using var json = JsonDocument.Parse(line[Prefix.Length..]);
            error = json.RootElement.GetProperty("Error").GetString() ?? "";
            return error.Length is > 0 and <= 4096;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
}
