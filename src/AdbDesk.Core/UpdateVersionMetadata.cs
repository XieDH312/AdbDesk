using System.Security.Cryptography;
using System.Text.Json;

namespace AdbDesk.Core;

/// <summary>Version identity authenticated by an unchanged schema-1 manifest file entry.</summary>
public sealed record UpdateVersionMetadata(long VersionCode, string VersionName)
{
    public const string FileName = "AdbDesk.version.json";
    public const int MaxBytes = 16384;

    internal static UpdateFile? FindFile(UpdateManifest manifest) => manifest.Files.SingleOrDefault(file => file.Path == FileName);

    internal static UpdateVersionMetadata Verify(byte[] bytes, UpdateManifest manifest)
    {
        var file = FindFile(manifest) ?? throw new InvalidDataException("已签名清单缺少数字版本元数据。");
        if (bytes.Length != file.Size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(file.Sha256)))
            throw new InvalidDataException("数字版本元数据大小或 SHA-256 与签名清单不符。");
        return Parse(bytes, manifest.Version);
    }

    internal static UpdateVersionMetadata Parse(byte[] bytes, string expectedVersion)
    {
        if (bytes.Length is 0 or > MaxBytes) throw new InvalidDataException("数字版本元数据大小无效。");
        try
        {
            var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
            using var document = JsonDocument.Parse(bytes.AsMemory(offset), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("数字版本元数据必须是对象。");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if ((property.Name != "versionCode" && property.Name != "versionName") || !names.Add(property.Name))
                    throw new InvalidDataException("数字版本元数据包含未知或重复字段。");
            if (!root.TryGetProperty("versionCode", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var value) || value <= 0 ||
                !root.TryGetProperty("versionName", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != expectedVersion)
                throw new InvalidDataException("数字版本号必须为正整数且版本名称必须与签名清单一致。");
            return new(value, expectedVersion);
        }
        catch (JsonException exception) { throw new InvalidDataException("数字版本元数据 JSON 无效。", exception); }
    }
}
