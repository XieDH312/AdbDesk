using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdbDesk.Core;

internal static class UpdateDownloadTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static Task VersionsAsync()
    {
        Check(UpdateVersion.Parse("1.2.0-beta.10").CompareTo(UpdateVersion.Parse("1.2.0-beta.2")) > 0, "numeric prerelease ordering");
        Check(UpdateVersion.Parse("1.2.0").CompareTo(UpdateVersion.Parse("1.2.0-beta.10")) > 0, "stable wins");
        Check(UpdateVersion.Parse("1.2.0+build.9").CompareTo(UpdateVersion.Parse("1.2.0+build.1")) == 0, "build metadata ignored");
        foreach (var invalid in new[] { "1.2", "01.2.3", "1.2.3-beta.01", "1.2.3-", "../1.2.3", "1.2.3\n" })
            Check(!UpdateVersion.TryParse(invalid, out _), "invalid SemVer " + invalid);
        return Task.CompletedTask;
    }

    public static Task ManifestAsync()
    {
        using var rsa = RSA.Create(2048);
        var manifest = Manifest("1.2.0", [1, 2, 3]);
        var pair = Sign(manifest, rsa);
        Check(UpdateManifestVerifier.Verify(pair.Bytes, pair.Signature, rsa.ExportSubjectPublicKeyInfoPem()).Version == "1.2.0", "valid signature");
        pair.Bytes[0] ^= 1;
        Reject(() => UpdateManifestVerifier.Verify(pair.Bytes, pair.Signature, rsa.ExportSubjectPublicKeyInfoPem()));
        foreach (var path in new[] { "../x", "a/../x", "a//b", "/x", "C:/x", "x:stream", "CON.txt", "a/NUL", "x.", "x ", "a/./b", "a\\..\\x", "COM¹.txt", "x\u0000" })
            Reject(() => UpdateManifestVerifier.ValidateFilePath(path));
        foreach (var broken in new[] {
            manifest with { Files = [.. manifest.Files, manifest.Files[0] with { Path = "adbdesk.EXE" }] },
            manifest with { Files = manifest.Files.Skip(1).ToArray() },
            manifest with { Sha256 = "123" }, manifest with { Size = 1073741825 },
            manifest with { Files = [.. manifest.Files, new("bad", -1, new string('a', 64))] } })
        {
            var signed = Sign(broken, rsa);
            Reject(() => UpdateManifestVerifier.Verify(signed.Bytes, signed.Signature, rsa.ExportSubjectPublicKeyInfoPem()));
        }
        return Task.CompletedTask;
    }

    public static async Task DiscoveryAsync()
    {
        using var rsa = RSA.Create(2048);
        var calls = new List<Uri>();
        var payload = new byte[] { 1, 2, 3 };
        using var client = Client(request =>
        {
            calls.Add(request.RequestUri!);
            if (request.RequestUri!.Host == "api.github.com") return Bytes(JsonSerializer.SerializeToUtf8Bytes(new[] { Release("1.3.0-beta.10", true), Release("1.2.0", false), Release("1.4.0", false, true) }));
            var version = request.RequestUri.AbsolutePath.Contains("1.3.0") ? "1.3.0-beta.10" : "1.2.0";
            var signed = Sign(Manifest(version, payload), rsa);
            return Bytes(request.RequestUri.AbsolutePath.EndsWith(".sig") ? signed.Signature : signed.Bytes);
        });
        using var service = new UpdateService(client, rsa.ExportSubjectPublicKeyInfoPem());
        Check((await service.CheckAsync("1.0.0", false, default))?.Manifest.Version == "1.2.0", "stable discovery");
        Check((await service.CheckAsync("1.0.0", true, default))?.Manifest.Version == "1.3.0-beta.10", "beta opt-in");
        Check(await service.CheckAsync("2.0.0", true, default) is null, "no downgrade");
        Check(calls.All(uri => !uri.AbsolutePath.EndsWith(".zip")), "check never downloads ZIP");
        using var maliciousClient = Client(request =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Bytes(JsonSerializer.SerializeToUtf8Bytes(new[] { Release("1.2.0", false, false, "https://evil.example/package.zip") }));
            var signed = Sign(Manifest("1.2.0", payload), rsa);
            return Bytes(request.RequestUri.AbsolutePath.EndsWith(".sig") ? signed.Signature : signed.Bytes);
        });
        using var maliciousService = new UpdateService(maliciousClient, rsa.ExportSubjectPublicKeyInfoPem());
        await RejectAsync(() => maliciousService.CheckAsync("1.0.0", false, default));
        using var mismatchClient = Client(request =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Bytes(JsonSerializer.SerializeToUtf8Bytes(new[] { Release("1.2.0", false) }));
            var signed = Sign(Manifest("1.1.0", payload), rsa);
            return Bytes(request.RequestUri.AbsolutePath.EndsWith(".sig") ? signed.Signature : signed.Bytes);
        });
        using var mismatchService = new UpdateService(mismatchClient, rsa.ExportSubjectPublicKeyInfoPem());
        await RejectAsync(() => mismatchService.CheckAsync("1.0.0", false, default));
    }

    public static async Task DownloadAsync()
    {
        using var rsa = RSA.Create(2048);
        var payload = new byte[] { 1, 2, 3 };
        var manifest = Manifest("1.2.0", payload);
        var signed = Sign(manifest, rsa);
        var update = new AvailableUpdate(manifest, signed.Bytes, signed.Signature, "", new("https://github.com/XieDH312/AdbDesk/releases/tag/v1.2.0"), new(AssetUrl("1.2.0", manifest.AssetName)));
        var cache = Path.Combine(Path.GetTempPath(), "AdbDesk-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        var unrelated = Path.Combine(cache, "keep.txt");
        await File.WriteAllTextAsync(unrelated, "keep");
        try
        {
            foreach (var data in new[] { new byte[] { 1, 2, 4 }, new byte[] { 1, 2 }, new byte[] { 1, 2, 3, 4 } })
            {
                using var badClient = Client(_ => Bytes(data));
                using var badService = new UpdateService(badClient, rsa.ExportSubjectPublicKeyInfoPem());
                await RejectAsync(() => badService.DownloadAsync(update, cache, null, default));
                Check(!Directory.EnumerateFiles(cache, "*.zip", SearchOption.AllDirectories).Any(), "failed ZIP removed");
            }
            using var cancellation = new CancellationTokenSource();
            using var cancelClient = Client(_ => { cancellation.Cancel(); return Bytes(payload); });
            using var cancelService = new UpdateService(cancelClient, rsa.ExportSubjectPublicKeyInfoPem());
            try { await cancelService.DownloadAsync(update, cache, null, cancellation.Token); throw new Exception("cancellation missing"); }
            catch (OperationCanceledException) { }
            Check(!Directory.EnumerateFiles(cache, "*.zip", SearchOption.AllDirectories).Any(), "cancelled ZIP removed");
            using var client = Client(_ => Bytes(payload));
            using var service = new UpdateService(client, rsa.ExportSubjectPublicKeyInfoPem());
            var result = await service.DownloadAsync(update, cache, null, default);
            Check((await File.ReadAllBytesAsync(result.ArchivePath)).SequenceEqual(payload), "verified bytes persisted");
            Check(File.Exists(result.ManifestPath) && File.Exists(result.SignaturePath) && File.Exists(unrelated), "proof and unrelated cache preserved");
            await RejectAsync(() => service.DownloadAsync(update with { Manifest = manifest with { Version = "9.9.9" } }, cache, null, default));
            using var redirectClient = Client(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://evil.example/payload") } });
            using var redirectService = new UpdateService(redirectClient, rsa.ExportSubjectPublicKeyInfoPem());
            await RejectAsync(() => redirectService.DownloadAsync(update, cache, null, default));
            var redirectRequests = 0;
            using var cdnClient = Client(request =>
            {
                redirectRequests++;
                return request.RequestUri!.Host == "github.com" ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/test") } } : Bytes(payload);
            });
            using var cdnService = new UpdateService(cdnClient, rsa.ExportSubjectPublicKeyInfoPem());
            await cdnService.DownloadAsync(update, cache, null, default);
            Check(redirectRequests == 2, "approved GitHub CDN redirect");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories)) File.Delete(file);
            foreach (var dir in Directory.EnumerateDirectories(cache)) Directory.Delete(dir);
            Directory.Delete(cache);
        }
    }

    private static UpdateManifest Manifest(string version, byte[] bytes) => new(1, "AdbDesk", version, $"AdbDesk-v{version}-win-x64.zip", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), new[] { "AdbDesk.exe", "AdbDesk.dll", "AdbDesk.deps.json", "AdbDesk.runtimeconfig.json", "AdbDesk.Core.dll", "AdbDesk.installation.json" }.Select(path => new UpdateFile(path, 1, new string('a', 64))).ToArray());
    private static (byte[] Bytes, byte[] Signature) Sign(UpdateManifest manifest, RSA rsa)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        return (bytes, Encoding.UTF8.GetBytes(Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
    }
    private static string AssetUrl(string version, string name) => $"https://github.com/XieDH312/AdbDesk/releases/download/v{version}/{name}";
    private static object Release(string version, bool prerelease, bool draft = false, string? zipUrl = null) => new { tag_name = "v" + version, prerelease, draft, body = "notes", html_url = $"https://github.com/XieDH312/AdbDesk/releases/tag/v{version}", assets = new[] { "update-manifest.json", "update-manifest.sig", $"AdbDesk-v{version}-win-x64.zip" }.Select(name => new { name, browser_download_url = name.EndsWith(".zip") && zipUrl != null ? zipUrl : AssetUrl(version, name) }).ToArray() };
    private static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Handler(send));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request)); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected rejection"); }
    private static async Task RejectAsync(Func<Task> action) { try { await action(); } catch (InvalidDataException) { return; } throw new Exception("Expected rejection"); }
}
