using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdbDesk.Core;

public sealed class UpdateService : IDisposable
{
    private const string Repository = "XieDH312/AdbDesk";
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly string publicKeyPem;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public UpdateService(HttpClient? client = null) : this(client, UpdateTrust.PublicKeyPem) { }
    internal UpdateService(HttpClient? client, string publicKeyPem)
    {
        this.publicKeyPem = publicKeyPem;
        ownsClient = client is null;
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<AvailableUpdate?> CheckAsync(string currentVersion, bool includePrereleases, CancellationToken token) =>
        CheckAsync(currentVersion, 0, includePrereleases, token);

    public async Task<AvailableUpdate?> CheckAsync(string currentVersion, long currentCode, bool includePrereleases, CancellationToken token)
    {
        if (currentCode < 0) throw new ArgumentOutOfRangeException(nameof(currentCode));
        var current = UpdateVersion.Parse(currentVersion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        // This budget covers the release list and authenticated metadata of every
        // candidate, including CDN redirects. It is not a per-request timeout.
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var checkingStep = "读取 GitHub 发行列表";
        try
        {
            var releases = new List<(JsonElement Release, UpdateVersion Version, string Text, string Tag)>();
            for (var page = 1; page <= 10; page++)
            {
                var url = new Uri($"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}");
                checkingStep = $"读取 GitHub 发行列表（第 {page} 页）";
                var bytes = await ReadBytesAsync(url, UpdateManifestVerifier.MaxManifestBytes, true, timeout.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub 更新列表格式无效。");
                foreach (var release in document.RootElement.EnumerateArray())
                {
                    if (Boolean(release, "draft")) continue;
                    var tag = String(release, "tag_name");
                    var text = tag.StartsWith('v') ? tag[1..] : tag;
                    if (!UpdateVersion.TryParse(text, out var version) || version is null || (currentCode == 0 && version.CompareTo(current) <= 0)) continue;
                    if (!includePrereleases && (Boolean(release, "prerelease") || version.IsPrerelease)) continue;
                    releases.Add((release.Clone(), version, text, tag));
                }
                if (document.RootElement.GetArrayLength() < 100) break;
            }
            AvailableUpdate? best = null;
            foreach (var candidate in releases.OrderByDescending(item => item.Version))
            {
                if (!candidate.Release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
                var assetList = assets.EnumerateArray().ToArray();
                var manifestAsset = FindAsset(assetList, "update-manifest.json");
                var signatureAsset = FindAsset(assetList, "update-manifest.sig");
                if (manifestAsset is null || signatureAsset is null) continue; // Older unsigned releases are not installable.
                var manifestUrl = AssetUri(manifestAsset.Value, candidate.Tag, "update-manifest.json");
                var signatureUrl = AssetUri(signatureAsset.Value, candidate.Tag, "update-manifest.sig");
                checkingStep = $"读取 {candidate.Text} 的 update-manifest.json";
                var manifestBytes = await ReadBytesAsync(manifestUrl, UpdateManifestVerifier.MaxManifestBytes, false, timeout.Token).ConfigureAwait(false);
                checkingStep = $"读取 {candidate.Text} 的 update-manifest.sig";
                var signatureBytes = await ReadBytesAsync(signatureUrl, 16384, false, timeout.Token).ConfigureAwait(false);
                var manifest = UpdateManifestVerifier.Verify(manifestBytes, signatureBytes, publicKeyPem);
                if (manifest.Version != candidate.Text || (currentCode == 0 && UpdateVersion.Parse(manifest.Version).CompareTo(current) <= 0)) throw new InvalidDataException("已签名的更新版本与 GitHub 标签不符或不是更高版本。");
                byte[]? versionBytes = null;
                long versionCode = 0;
                if (UpdateVersionMetadata.FindFile(manifest) is not null)
                {
                    var versionAsset = FindAsset(assetList, UpdateVersionMetadata.FileName) ?? throw new InvalidDataException("此版本缺少已签名的数字版本附件。");
                    checkingStep = $"读取 {candidate.Text} 的 {UpdateVersionMetadata.FileName}";
                    versionBytes = await ReadBytesAsync(AssetUri(versionAsset, candidate.Tag, UpdateVersionMetadata.FileName), UpdateVersionMetadata.MaxBytes, false, timeout.Token).ConfigureAwait(false);
                    versionCode = UpdateVersionMetadata.Verify(versionBytes, manifest).VersionCode;
                }
                // A modern client cannot infer an order from names on an older release.
                if (currentCode > 0 && versionCode <= currentCode) continue;
                var package = FindAsset(assetList, manifest.AssetName) ?? throw new InvalidDataException("此版本缺少已签名清单指定的安装包。");
                var packageUrl = AssetUri(package, candidate.Tag, manifest.AssetName);
                // Construct the release link from the validated repository/tag, never from arbitrary release text.
                var available = new AvailableUpdate(manifest, manifestBytes, signatureBytes, String(candidate.Release, "body"), new Uri($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(candidate.Tag)}"), packageUrl, versionCode, versionBytes);
                if (currentCode == 0) return available;
                if (best is null || available.VersionCode > best.VersionCode) best = available;
            }
            return best;
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested) { throw new IOException($"检查更新超时（总时限 60 秒；{checkingStep}）。请检查网络或系统代理后重试。", exception); }
        catch (HttpRequestException exception) { throw new IOException($"无法连接 GitHub 检查更新（{checkingStep}）。请检查网络或系统代理。", exception); }
        catch (JsonException exception) { throw new InvalidDataException("GitHub 返回了无效的更新数据。", exception); }
    }

    public async Task<DownloadedUpdate> DownloadAsync(AvailableUpdate update, string cacheRoot, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var manifestBytes = update.ManifestBytes.ToArray();
        var signatureBytes = update.SignatureBytes.ToArray();
        var manifest = UpdateManifestVerifier.Verify(manifestBytes, signatureBytes, publicKeyPem);
        if (!JsonSerializer.SerializeToUtf8Bytes(manifest, Json).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(update.Manifest, Json))) throw new InvalidDataException("更新信息已发生变化，请重新检查更新。");
        var versionBytes = update.VersionMetadataBytes?.ToArray();
        long versionCode = 0;
        if (UpdateVersionMetadata.FindFile(manifest) is not null)
            versionCode = UpdateVersionMetadata.Verify(versionBytes ?? throw new InvalidDataException("数字版本元数据丢失，请重新检查更新。"), manifest).VersionCode;
        else if (versionBytes is not null) throw new InvalidDataException("数字版本元数据未受签名清单保护。");
        if (versionCode != update.VersionCode) throw new InvalidDataException("数字版本信息已发生变化，请重新检查更新。");
        ValidatePackageUri(update.PackageUrl, manifest);
        var root = Path.GetFullPath(cacheRoot);
        RejectReparseAncestors(root);
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, "update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var archivePath = Path.Combine(directory, manifest.AssetName);
        var manifestPath = Path.Combine(directory, "update-manifest.json");
        var signaturePath = Path.Combine(directory, "update-manifest.sig");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            RejectReparseAncestors(directory);
            using (var response = await SendAsync(update.PackageUrl, false, timeout.Token).ConfigureAwait(false))
            {
                if (response.Content.Headers.ContentLength is long length && length != manifest.Size) throw new InvalidDataException("更新安装包大小与签名清单不符。");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long received = 0;
                progress?.Report(new UpdateProgress(0, manifest.Size, "正在下载更新…"));
                while (true)
                {
                    var count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > manifest.Size || received > UpdateManifestVerifier.MaxArchiveBytes) throw new InvalidDataException("更新安装包超过签名清单声明的大小。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    progress?.Report(new UpdateProgress(received, manifest.Size, "正在下载更新…"));
                }
                if (received != manifest.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(manifest.Sha256))) throw new InvalidDataException("更新安装包不完整或 SHA-256 校验失败，请重新下载。");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            await WriteNewAsync(manifestPath, manifestBytes, timeout.Token).ConfigureAwait(false);
            await WriteNewAsync(signaturePath, signatureBytes, timeout.Token).ConfigureAwait(false);
            progress?.Report(new UpdateProgress(manifest.Size, manifest.Size, "下载完成，签名和 SHA-256 校验通过。"));
            return new DownloadedUpdate(directory, archivePath, manifestPath, signaturePath, manifest.Version, versionCode);
        }
        catch (Exception exception)
        {
            // Delete only the three files owned by this attempt; never recursively clean the cache.
            foreach (var path in new[] { archivePath, manifestPath, signaturePath }) { try { RejectReparseAncestors(directory); File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            try { Directory.Delete(directory, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (exception is OperationCanceledException && !token.IsCancellationRequested) throw new IOException("下载更新超时，请检查网络后重试。", exception);
            if (exception is HttpRequestException) throw new IOException("下载更新失败，请检查网络或系统代理后重试。", exception);
            throw;
        }
    }

    private async Task<byte[]> ReadBytesAsync(Uri uri, int maxBytes, bool metadata, CancellationToken token)
    {
        using var response = await SendAsync(uri, metadata, token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("更新元数据超过允许大小。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > maxBytes) throw new InvalidDataException("更新元数据超过允许大小。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool metadata, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("AdbDesk-Updater/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(metadata ? "application/vnd.github+json" : "application/octet-stream"));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var actualUri = response.RequestMessage?.RequestUri;
            if (actualUri != null && actualUri != uri && !AllowedRedirect(actualUri, metadata)) { response.Dispose(); throw new InvalidDataException("更新下载被重定向到不受信任的地址。"); }
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                var next = location is null ? null : location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                if (next is null || !AllowedRedirect(next, metadata)) throw new InvalidDataException("GitHub 更新重定向地址不受信任。");
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw new IOException("GitHub 访问受限或请求次数达到上限，请稍后重试。");
                throw new HttpRequestException($"GitHub 返回 HTTP {(int)status}。", null, status);
            }
            return response;
        }
        throw new InvalidDataException("GitHub 更新重定向次数过多。");
    }

    private static bool AllowedRedirect(Uri uri, bool metadata) => SafeHttps(uri) && (metadata
        ? uri.Host == "api.github.com" && uri.AbsolutePath.StartsWith($"/repos/{Repository}/releases", StringComparison.Ordinal)
        : uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com");
    private static bool SafeHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    private static Uri AssetUri(JsonElement asset, string tag, string name)
    {
        if (!Uri.TryCreate(String(asset, "browser_download_url"), UriKind.Absolute, out var uri) || !SafeHttps(uri) || uri.Host != "github.com" || uri.Query.Length != 0 || uri.AbsolutePath != $"/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}") throw new InvalidDataException("更新资源地址不属于官方 GitHub 仓库。");
        return uri;
    }
    private static void ValidatePackageUri(Uri uri, UpdateManifest manifest)
    {
        if (!SafeHttps(uri) || uri.Host != "github.com" || uri.Query.Length != 0 || !new[] { manifest.Version, "v" + manifest.Version }.Any(tag => uri.AbsolutePath == $"/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(manifest.AssetName)}")) throw new InvalidDataException("更新安装包地址与签名版本不符。");
    }
    private static JsonElement? FindAsset(JsonElement[] assets, string name)
    {
        var matching = assets.Where(asset => String(asset, "name") == name).ToArray();
        if (matching.Length > 1) throw new InvalidDataException("GitHub 更新资源名称重复。");
        return matching.Length == 0 ? null : matching[0];
    }
    private static string String(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Boolean(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    private static void RejectReparseAncestors(string directory)
    {
        for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("更新缓存路径不能经过符号链接或目录联接。");
    }
    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }
    public void Dispose() { if (ownsClient) client.Dispose(); }
}
