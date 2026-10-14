using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdbDesk.Core;

public static class UpdateManifestVerifier
{
    public const int MaxManifestBytes = 4 * 1024 * 1024;
    public const int MaxFiles = 10000;
    public const long MaxArchiveBytes = 1024L * 1024 * 1024;
    public const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, MaxDepth = 16, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private static readonly string[] RequiredFiles = ["AdbDesk.exe", "AdbDesk.dll", "AdbDesk.deps.json", "AdbDesk.runtimeconfig.json", "AdbDesk.Core.dll", "AdbDesk.installation.json"];

    public static UpdateManifest Verify(byte[] manifestBytes, byte[] signatureBytes) => Verify(manifestBytes, signatureBytes, UpdateTrust.PublicKeyPem);
    internal static UpdateManifest Verify(byte[] manifestBytes, byte[] signatureBytes, string publicKeyPem)
    {
        if (manifestBytes.Length is 0 or > MaxManifestBytes || signatureBytes.Length is 0 or > 16384) throw new InvalidDataException("更新清单或签名超过允许大小。");
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            var signature = Convert.FromBase64String(new UTF8Encoding(false, true).GetString(signatureBytes).Trim());
            if (rsa.KeySize < 2048 || !rsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new InvalidDataException("更新签名校验失败，已拒绝此更新。");
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        { throw new InvalidDataException("更新签名无效或本机更新公钥未配置。", exception); }
        UpdateManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, Json) ?? throw new InvalidDataException("更新清单为空。");
        }
        catch (JsonException exception) { throw new InvalidDataException("更新清单格式无效。", exception); }
        if (manifest.SchemaVersion != 1 || manifest.ApplicationId != "AdbDesk" || !UpdateVersion.TryParse(manifest.Version, out _) || manifest.AssetName != $"AdbDesk-v{manifest.Version}-win-x64.zip") throw new InvalidDataException("更新清单的应用、版本或安装包名称不匹配。");
        if (manifest.Size <= 0 || manifest.Size > MaxArchiveBytes || !ValidHash(manifest.Sha256)) throw new InvalidDataException("更新安装包大小或 SHA-256 无效。");
        if (manifest.Files is null || manifest.Files.Count is 0 or > MaxFiles) throw new InvalidDataException("更新文件数量超出限制。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null) throw new InvalidDataException("更新文件条目为空。");
            var path = ValidateFilePath(file.Path);
            if (!paths.Add(path) || file.Size < 0 || file.Size > MaxExtractedBytes || !ValidHash(file.Sha256)) throw new InvalidDataException("更新文件存在重复路径、无效大小或 SHA-256。");
            total += file.Size;
            if (total > MaxExtractedBytes) throw new InvalidDataException("解压后的更新大小超过 2 GiB 限制。");
        }
        foreach (var path in paths)
        {
            var slash = path.IndexOf('/');
            while (slash >= 0) { if (paths.Contains(path[..slash])) throw new InvalidDataException("更新文件和目录路径冲突。"); slash = path.IndexOf('/', slash + 1); }
        }
        if (RequiredFiles.Any(path => !paths.Contains(path))) throw new InvalidDataException("更新缺少必需的程序文件或安装标记。");
        return manifest;
    }

    public static string ValidateFilePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240) throw new InvalidDataException("更新文件路径为空或过长。");
        var normalized = path.Replace('\\', '/');
        foreach (var segment in normalized.Split('/'))
        {
            if (segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') || segment.Any(c => c < 32 || "<>:\"|?*".Contains(c))) throw new InvalidDataException("更新包含不安全的文件路径。");
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "0123456789¹²³".Contains(stem[3]))) throw new InvalidDataException("更新路径包含 Windows 保留设备名称。");
        }
        return normalized;
    }
    private static bool ValidHash(string? value) => value?.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new InvalidDataException("更新清单包含重复字段。"); RejectDuplicateProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
