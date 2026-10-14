using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AdbDesk.Core;

internal static class NetworkCaptureExportTests
{
    private static NetworkCaptureEntry Entry() => new("id", DateTimeOffset.Parse("2026-09-29T00:00:00Z"), "POST", "https://example.test/a?q=1&q=2", 200, 12, "", "preview-only", "", "preview-only", "")
    {
        RequestHeaderItems = [new("Content-Type", "application/json"), new("X-Test", "one"), new("X-Test", "two")],
        ResponseHeaderItems = [new("Content-Type", "application/json"), new("Set-Cookie", "a=1"), new("Set-Cookie", "b=2")],
        RequestHeadersComplete = true, ResponseHeadersComplete = true, Finished = true, HttpVersion = "HTTP/1.1",
        RequestCapture = new(null, 0, 0, true, ""), ResponseCapture = new(null, 0, 0, true, "")
    };
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException) { return; } throw new Exception("Expected unsafe/incomplete export rejection"); }
    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "adbdesk-export-" + Guid.NewGuid()); Directory.CreateDirectory(p); return p; }
    private static CapturedBody Save(string dir, byte[] bytes, string name = "body.bin") { File.WriteAllBytes(Path.Combine(dir, name), bytes); return new(name, bytes.Length, bytes.Length, true, ""); }

    public static Task CurlSafetyAsync()
    {
        var dir = Temp();
        try
        {
            var entry = Entry() with { RequestCapture = Save(dir, [0, 255, 42]), RequestHeaderItems = [new("X-Test", "a'b $x \"q\""), new("Connection", "X-Hop"), new("X-Hop", "gone"), new("Content-Length", "3"), new("Content-Encoding", "gzip")] };
            var curl = NetworkCaptureExport.CreatePowerShellCurl(entry, dir);
            Check(curl.Contains("curl.exe --disable") && curl.Contains("--globoff") && curl.Contains("--url 'https://") && curl.Contains("a''b $x \"q\""), "PowerShell literal quoting and explicit URL");
            Check(curl.Contains("--data-binary '@") && curl.Contains("Content-Encoding: gzip") && !curl.Contains("X-Hop:") && !curl.Contains("Content-Length:"), "raw file and hop filtering");
            Check(NetworkCaptureExport.CreatePowerShellCurl(Entry() with { Url = "https://example.test/a'b?x=$foo&y=[1]" }, dir).Contains("--url 'https://example.test/a''b?x=$foo&y=[1]'"), "URL shell metacharacters remain a single literal argument");
            var dotPath = NetworkCaptureExport.CreatePowerShellCurl(Entry() with { Url = "https://example.test/a/../b" }, dir);
            Check(dotPath.Contains("--path-as-is") && dotPath.Contains("--url 'https://example.test/a/../b'"), "curl preserves captured dot path segments");
            Reject(() => NetworkCaptureExport.CreatePowerShellCurl(entry with { Method = "GET\r\nX:" }, dir));
            Reject(() => NetworkCaptureExport.CreatePowerShellCurl(entry with { Url = "file:///C:/secret" }, dir));
            Reject(() => NetworkCaptureExport.CreatePowerShellCurl(entry with { Url = "https://example.test/\r\n" }, dir));
            Reject(() => NetworkCaptureExport.CreatePowerShellCurl(entry with { RequestHeaderItems = [new("X-Test", "ok\r\nInjected: yes")] }, dir));
            Reject(() => NetworkCaptureExport.CreatePowerShellCurl(entry with { RequestHeaderItems = [new("@file", "bad")] }, dir));
            Reject(() => NetworkCaptureExport.CreatePowerShellCurl(entry with { RequestHeaderItems = [new("X-Test", "smart\u2019quote")] }, dir));
            Reject(() => NetworkCaptureExport.CreatePowerShellCurl(entry with { RequestHeadersComplete = false }, dir));
            Reject(() => NetworkCaptureExport.CreatePowerShellCurl(entry with { Url = "https://example.test/" + new string('a', 16000), RequestTargetComplete = false }, dir));
            Reject(() => NetworkCaptureExport.CreatePowerShellCurl(entry with { RequestCapture = null }, dir));
            Reject(() => NetworkCaptureExport.CreatePowerShellCurl(entry with { RequestCapture = new("../secret", 3, 3, true, "") }, dir));
            Reject(() => NetworkCaptureExport.CreatePowerShellCurl(entry with { RequestCapture = entry.RequestCapture! with { Complete = false } }, dir));
            Check(!NetworkCaptureExport.CreatePowerShellCurl(Entry(), dir).Contains("--data-binary"), "verified empty body needs no file");
            Check(NetworkCaptureExport.CreatePowerShellCurl(Entry() with { Method = "HEAD" }, dir).Contains(" --head"), "HEAD configures curl response-body semantics");
            Reject(() => NetworkCaptureExport.CreatePowerShellCurl(entry with { Method = "HEAD" }, dir));
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }

    public static async Task HarFidelityAsync()
    {
        var dir = Temp();
        try
        {
            var raw = Encoding.UTF8.GetBytes("{\"ok\":true}");
            var entry = Entry() with { RequestCapture = Save(dir, raw, "request.bin"), ResponseCapture = Save(dir, Compress(raw), "response.bin"), ResponseHeaderItems = [new("Content-Type", "application/json"), new("Content-Encoding", "gzip"), new("Set-Cookie", "a=1"), new("Set-Cookie", "b=2")] };
            var incomplete = entry with { Finished = false, RequestTargetComplete = false, ResponseCapture = null, RequestCapture = new(null, 5, 0, false, "disconnected"), StatusCode = null };
            var path = Path.Combine(dir, "out.har");
            await NetworkCaptureExport.WriteHarAsync(path, new[] { entry, incomplete }, dir);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var entries = json.RootElement.GetProperty("log").GetProperty("entries");
            var first = entries[0];
            Check(first.GetProperty("request").GetProperty("queryString").GetArrayLength() == 2, "duplicate query retained");
            Check(first.GetProperty("request").GetProperty("headers").EnumerateArray().Count(h => h.GetProperty("name").GetString() == "X-Test") == 2, "duplicate request headers retained");
            Check(first.GetProperty("response").GetProperty("headers").EnumerateArray().Count(h => h.GetProperty("name").GetString() == "Set-Cookie") == 2, "duplicate response headers retained");
            var content = first.GetProperty("response").GetProperty("content");
            Check(content.GetProperty("text").GetString() == "{\"ok\":true}" && content.GetProperty("size").GetInt32() == raw.Length, "HAR has decoded content");
            Check(content.GetProperty("compression").GetInt64() == raw.Length - entry.ResponseCapture!.TotalBytes, "HAR compression uses raw wire length");
            Check(first.GetProperty("timings").GetProperty("wait").GetInt32() == -1, "unknown timing is not invented");
            Check(!entries[1].GetProperty("response").GetProperty("content").TryGetProperty("text", out _), "missing body is not preview");
            Check(entries[1].GetProperty("time").GetDouble() == -1 && entries[1].GetProperty("_finished").GetBoolean() == false, "unfinished transaction remains explicit");
            Check(!entries[1].GetProperty("request").GetProperty("_requestTargetComplete").GetBoolean() && entries[1].GetProperty("request").GetProperty("_requestTargetComment").GetString()!.Contains("truncated"), "truncated method or URL cannot masquerade as a full target");
            await File.WriteAllTextAsync(path, "preserve");
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { await NetworkCaptureExport.WriteHarAsync(path, new[] { entry }, dir, cts.Token); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
            Check(await File.ReadAllTextAsync(path) == "preserve" && Directory.GetFiles(dir, "*.tmp").Length == 0, "canceled HAR preserves destination and removes partial file");
            IEnumerable<NetworkCaptureEntry> BrokenHistory() { yield return entry; throw new InvalidDataException("Corrupt next entry"); }
            try { await NetworkCaptureExport.WriteHarAsync(path, BrokenHistory(), dir); throw new Exception("Expected broken history failure"); } catch (InvalidDataException) { }
            Check(await File.ReadAllTextAsync(path) == "preserve" && Directory.GetFiles(dir, "*.tmp").Length == 0, "mid enumeration failure cannot publish partial HAR");
        }
        finally { Directory.Delete(dir, true); }
    }

    public static Task BodyAndFiltersAsync()
    {
        var dir = Temp();
        try
        {
            var entry = Entry() with { ResponseCapture = Save(dir, Compress(Encoding.UTF8.GetBytes("{\"a\":1}"))), ResponseHeaderItems = [new("Content-Type", "application/json"), new("Content-Encoding", "gzip")] };
            Check(NetworkCaptureView.ReadBodyText(entry, true, dir) == "{\"a\":1}", "text decode is JSON ready");
            foreach (var coding in new[] { "deflate", "br" })
            {
                using var compressed = new MemoryStream();
                using (Stream encoder = coding == "br" ? new BrotliStream(compressed, CompressionLevel.Fastest, true) : new ZLibStream(compressed, CompressionLevel.Fastest, true)) encoder.Write(Encoding.UTF8.GetBytes("hello"));
                var other = entry with { ResponseCapture = Save(dir, compressed.ToArray(), coding + ".bin"), ResponseHeaderItems = [new("Content-Type", "text/plain"), new("Content-Encoding", coding)] };
                Check(NetworkCaptureView.ReadBodyText(other, true, dir) == "hello", coding + " is decoded");
            }
            Check(NetworkCaptureView.FormatJson("{\"a\":1}").Contains("\n") && NetworkCaptureView.FormatJson("invalid") == "invalid", "JSON format preserves non JSON");
            var binary = entry with { ResponseCapture = Save(dir, [0, 255], "binary.bin"), ResponseHeaderItems = [new("Content-Type", "application/octet-stream")] };
            Check(NetworkCaptureView.ReadBodyText(binary, true, dir).Contains("base64") && NetworkCaptureView.ReadBodyText(binary, true, dir).Contains("AP8="), "binary explicit base64");
            Check(NetworkCaptureView.ReadBodyText(entry with { ResponseCapture = null }, true, dir).Contains("preview-only"), "missing body clearly shows preview fallback");
            var bomb = entry with { ResponseCapture = Save(dir, Compress(new byte[4 * 1024 * 1024 + 1]), "bomb.bin") };
            Check(NetworkCaptureView.ReadBodyText(bomb, true, dir).Length < 1000, "decompression limit bounds output");
            Check(NetworkCaptureView.Matches(entry, "example", "example.test", "POST", "2xx", "json", false, 10), "combined filters");
            Check(!NetworkCaptureView.Matches(entry, "", "ample.test", "", "", "", false, null), "domain matches label boundaries");
            Check(NetworkCaptureView.Matches(entry with { Url = "https://sub.example.test/", StatusCode = 500 }, "", "example.test", "", "5xx", "", true, null), "subdomains and failures");
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }
    private static byte[] Compress(byte[] data) { using var target = new MemoryStream(); using (var zip = new GZipStream(target, CompressionLevel.Fastest, true)) zip.Write(data); return target.ToArray(); }
}
