using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdbDesk.Core;

internal static class UpdateVersionCodeTests
{
    // Catches a shared 15-second deadline rejecting valid metadata on a slow connection.
    public static async Task SlowNetworkAsync()
    {
        using var fixture = new Fixture(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com") await Task.Delay(TimeSpan.FromSeconds(16), token);
        });
        fixture.Add("1.0.0-beta.7", 10007, true);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Check((await fixture.Service.CheckAsync("1.0.0-beta.6", 10006, true, caller.Token))?.VersionCode == 10007,
            "a slow successful metadata response is not confused with an unavailable update");
    }

    // Catches diagnostics losing the failing step, and caller cancellation becoming a network error.
    public static async Task RequestDiagnosticsAsync()
    {
        foreach (var target in new[] { "releases?", "update-manifest.json", "update-manifest.sig", "AdbDesk.version.json" })
        {
            using var fixture = new Fixture((request, _) =>
            {
                if (request.RequestUri!.AbsoluteUri.Contains(target, StringComparison.Ordinal))
                    throw new HttpRequestException("Test transport failure.");
                return Task.CompletedTask;
            });
            fixture.Add("1.0.0-beta.7", 10007, true);
            try { await fixture.Service.CheckAsync("1.0.0-beta.6", 10006, true, default); throw new Exception("Expected request failure"); }
            catch (IOException exception)
            {
                var expected = target == "releases?" ? "发行列表" : target;
                Check(exception.Message.Contains(expected, StringComparison.Ordinal), "diagnostic names the failing metadata step");
            }
        }
        using var cancellation = new CancellationTokenSource();
        using var canceledFixture = new Fixture((_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        try { await canceledFixture.Service.CheckAsync("1.0.0-beta.6", 10006, true, cancellation.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
    }

    // Catches accidentally retaining SemVer ordering or ignoring the release channel.
    public static async Task DiscoveryAsync()
    {
        using var fixture = new Fixture();
        fixture.Add("9.0.0", 99);
        fixture.Add("1.0.0", 102);
        fixture.Add("2.0.0", 101);
        fixture.Add("1.0.0-beta.9", 103, true);
        Check((await fixture.Service.CheckAsync("8.0.0", 100, false, default))?.VersionCode == 102, "highest code wins regardless of name");
        Check((await fixture.Service.CheckAsync("8.0.0", 100, true, default))?.VersionCode == 103, "prerelease opt-in applies before ordering");
        Check(await fixture.Service.CheckAsync("0.1.0", 103, true, default) is null, "equal and lower codes are not newer");
        Check((await fixture.Service.CheckAsync("8.0.0", false, default))?.Manifest.Version == "9.0.0", "legacy caller retains SemVer behavior");
    }

    // Catches accepting unsigned release metadata, malformed codes, and name substitution.
    public static async Task MetadataAsync()
    {
        foreach (var text in new[] { "{\"versionCode\":0,\"versionName\":\"1.0.0\"}", "{\"versionCode\":-1,\"versionName\":\"1.0.0\"}",
            "{\"versionCode\":9223372036854775808,\"versionName\":\"1.0.0\"}", "{\"versionCode\":\"102\",\"versionName\":\"1.0.0\"}",
            "{\"versionCode\":102,\"versionName\":\"9.0.0\"}", "{\"versionCode\":102,\"versionCode\":103,\"versionName\":\"1.0.0\"}",
            "{\"versionCode\":102.5,\"versionName\":\"1.0.0\"}", "{\"versionCode\":102}", "{\"versionCode\":102,\"versionName\":\"1.0.0\",\"extra\":true}" })
        {
            using var fixture = new Fixture();
            fixture.Add("1.0.0", 102, metadata: Encoding.UTF8.GetBytes(text));
            await RejectAsync(() => fixture.Service.CheckAsync("0.1.0", 100, true, default));
        }
        using (var fixture = new Fixture())
        {
            fixture.Add("1.0.0", long.MaxValue);
            Check((await fixture.Service.CheckAsync("0.1.0", 100, true, default))?.VersionCode == long.MaxValue, "full positive Int64 supported");
            fixture.Data["v1.0.0/AdbDesk.version.json"] = Encoding.UTF8.GetBytes("{\"versionCode\":999,\"versionName\":\"1.0.0\"}");
            await RejectAsync(() => fixture.Service.CheckAsync("0.1.0", 100, true, default));
        }
        using (var fixture = new Fixture())
        {
            fixture.Add("9.0.0", 102, omitMetadata: true);
            Check(await fixture.Service.CheckAsync("0.1.0", 100, true, default) is null, "modern clients skip true legacy manifests without falling back");
            Check(await fixture.Service.CheckAsync("9.0.0", 100, true, default) is null, "equal-name legacy release is ignored without fallback");
            Check(await fixture.Service.CheckAsync("10.0.0", 100, true, default) is null, "older legacy release is ignored without fallback");
        }
        using (var fixture = new Fixture())
        {
            fixture.Add("9.0.0", 102, omitMetadataAsset: true);
            try
            {
                await fixture.Service.CheckAsync("0.1.0", 100, true, default);
                throw new Exception("An incomplete future release must not report that the client is up to date");
            }
            catch (InvalidDataException exception)
            {
                Check(exception.Message.Contains("缺少已签名的数字版本附件", StringComparison.Ordinal), "signed metadata entry without an asset produces an explicit diagnostic");
            }
            await RejectAsync(() => fixture.Service.CheckAsync("10.0.0", 100, true, default));
        }
        using (var fixture = new Fixture())
        {
            fixture.Add("1.0.0", 102, metadata: [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("{\"versionCode\":102,\"versionName\":\"1.0.0\"}")]);
            Check((await fixture.Service.CheckAsync("0.1.0", 100, true, default))?.VersionCode == 102, "signed BOM bytes are authenticated before parsing");
        }
    }

    // Catches trusting mutable AvailableUpdate values between discovery and download.
    public static async Task DownloadAsync()
    {
        using var fixture = new Fixture();
        fixture.Add("1.0.0", 102);
        var update = (await fixture.Service.CheckAsync("9.0.0", 100, false, default))!;
        var cache = Path.Combine(Path.GetTempPath(), "AdbDesk-code-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await RejectAsync(() => fixture.Service.DownloadAsync(update with { VersionCode = 999 }, cache, null, default));
            await RejectAsync(() => fixture.Service.DownloadAsync(update with { VersionMetadataBytes = null }, cache, null, default));
            var downloaded = await fixture.Service.DownloadAsync(update, cache, null, default);
            Check(downloaded.VersionCode == 102, "download retains authenticated code");
            update.VersionMetadataBytes![0] ^= 1;
            await RejectAsync(() => fixture.Service.DownloadAsync(update, cache, null, default));
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }

    // Exercises the common Stage/Apply policy on real on-disk metadata, including re-reading tampered payloads.
    public static async Task InstallerPolicyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "AdbDesk-code-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = Encoding.UTF8.GetBytes("{\"versionCode\":102,\"versionName\":\"1.0.0\"}");
            var manifest = Fixture.Manifest("1.0.0", bytes);
            var path = Path.Combine(root, "AdbDesk.version.json");
            await File.WriteAllBytesAsync(path, bytes);
            await new UpdateInstaller("1.0.0", 102).ValidateInstalledVersionAsync(root, default);
            await RejectAsync(() => new UpdateInstaller("1.0.0", 101).ValidateInstalledVersionAsync(root, default));
            await RejectAsync(() => new UpdateInstaller("9.0.0", 102).ValidateInstalledVersionAsync(root, default));
            Check(await UpdateInstaller.ValidatePayloadVersionAsync(manifest, root, "9.0.0", 100, default) == 102, "installer permits higher code and lower name");
            await RejectAsync(() => UpdateInstaller.ValidatePayloadVersionAsync(manifest, root, "0.1.0", 102, default));
            await RejectAsync(() => UpdateInstaller.ValidatePayloadVersionAsync(manifest, root, "0.1.0", 103, default));
            await File.WriteAllTextAsync(path, "{\"versionCode\":999,\"versionName\":\"1.0.0\"}");
            await RejectAsync(() => UpdateInstaller.ValidatePayloadVersionAsync(manifest, root, "9.0.0", 100, default));
            var legacy = manifest with { Files = manifest.Files.Where(file => file.Path != "AdbDesk.version.json").ToArray() };
            await RejectAsync(() => UpdateInstaller.ValidatePayloadVersionAsync(legacy, root, "0.1.0", 100, default));
            Check(await UpdateInstaller.ValidatePayloadVersionAsync(legacy, root, "0.1.0", 0, default) == 0, "legacy installation accepts old signed package");
            await RejectAsync(() => UpdateInstaller.ValidatePayloadVersionAsync(legacy, root, "9.0.0", 0, default));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RSA rsa = RSA.Create(2048);
        private readonly HttpClient client;
        private readonly List<object> releases = [];
        public Dictionary<string, byte[]> Data { get; } = [];
        public UpdateService Service { get; }
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task>? beforeSend = null)
        {
            client = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com"
                ? JsonSerializer.SerializeToUtf8Bytes(releases)
                : Data[Uri.UnescapeDataString(request.RequestUri.AbsolutePath.Split("/releases/download/")[1])], beforeSend));
            Service = new UpdateService(client, rsa.ExportSubjectPublicKeyInfoPem());
        }
        public void Add(string version, long code, bool prerelease = false, byte[]? metadata = null, bool omitMetadata = false, bool omitMetadataAsset = false)
        {
            metadata ??= JsonSerializer.SerializeToUtf8Bytes(new { versionCode = code, versionName = version });
            var manifest = Manifest(version, metadata);
            if (omitMetadata) manifest = manifest with { Files = manifest.Files.Where(file => file.Path != "AdbDesk.version.json").ToArray() };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Data[$"v{version}/update-manifest.json"] = bytes;
            Data[$"v{version}/update-manifest.sig"] = Encoding.UTF8.GetBytes(Convert.ToBase64String(rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
            Data[$"v{version}/AdbDesk.version.json"] = metadata;
            Data[$"v{version}/{manifest.AssetName}"] = [1, 2, 3];
            var names = new List<string> { "update-manifest.json", "update-manifest.sig", manifest.AssetName };
            if (!omitMetadata && !omitMetadataAsset) names.Add("AdbDesk.version.json");
            releases.Add(new { tag_name = "v" + version, prerelease, draft = false, body = "notes", assets = names.Select(name => new { name, browser_download_url = $"https://github.com/XieDH312/AdbDesk/releases/download/v{version}/{name}" }).ToArray() });
        }
        public static UpdateManifest Manifest(string version, byte[] metadata) => new(1, "AdbDesk", version, $"AdbDesk-v{version}-win-x64.zip", 3,
            Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })), [.. new[] { "AdbDesk.exe", "AdbDesk.dll", "AdbDesk.deps.json", "AdbDesk.runtimeconfig.json", "AdbDesk.Core.dll", "AdbDesk.installation.json" }.Select(path => new UpdateFile(path, 1, new string('a', 64))), new("AdbDesk.version.json", metadata.Length, Convert.ToHexString(SHA256.HashData(metadata)))]);
        public void Dispose() { Service.Dispose(); client.Dispose(); rsa.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, byte[]> send, Func<HttpRequestMessage, CancellationToken, Task>? beforeSend) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (beforeSend is not null) await beforeSend(request, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(send(request)) };
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task RejectAsync(Func<Task> action) { try { await action(); } catch (InvalidDataException) { return; } throw new Exception("Expected unsafe version metadata to be rejected."); }
}
