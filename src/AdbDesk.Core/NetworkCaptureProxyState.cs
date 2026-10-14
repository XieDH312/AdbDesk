using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdbDesk.Core;

internal sealed class NetworkCaptureProxyState(string stateRoot, Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> adb)
{
    private static readonly string[] Keys = ["http_proxy", "global_http_proxy_host", "global_http_proxy_port", "global_http_proxy_exclusion_list", "global_proxy_pac_url"];
    internal sealed record Journal(string Serial, int Port, Dictionary<string, string?> Original);
    internal static string DeviceKey(string serial) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial)));
    private string JournalPath(string serial) => Path.Combine(stateRoot, "network-recovery-" + DeviceKey(serial) + ".json");
    public bool HasPendingRecovery(string serial) => File.Exists(JournalPath(serial));

    public async Task ConfigureAsync(string serial, int port, CancellationToken token)
    {
        if (HasPendingRecovery(serial)) throw new InvalidOperationException("该设备有未恢复的代理设置，请先恢复。");
        var original = await ReadAsync(serial, token).ConfigureAwait(false);
        // A legacy http_proxy write can also rewrite the modern proxy keys in Android's
        // observer. Refuse existing active/PAC configurations rather than approximating them.
        if (!IsEmptyProxy(original)) throw new InvalidOperationException("设备已有全局代理、PAC 或排除配置；请保留它并使用手动代理模式，或先自行关闭原代理。");
        Directory.CreateDirectory(stateRoot);
        var journal = new Journal(serial, port, original);
        await WriteDurableAsync(JournalPath(serial), journal, token).ConfigureAwait(false);
        await PutAsync(serial, "http_proxy", "127.0.0.1:" + port, token).ConfigureAwait(false);
        var current = await ReadAsync(serial, token).ConfigureAwait(false);
        if (current["http_proxy"] != "127.0.0.1:" + port)
            throw new IOException("设备未确认代理设置，已保留恢复记录。");
    }

    public async Task RestoreAsync(string serial, CancellationToken token)
    {
        var path = JournalPath(serial);
        if (!File.Exists(path)) return;
        var journal = JsonSerializer.Deserialize<Journal>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false))
            ?? throw new InvalidDataException("代理恢复记录损坏。");
        if (journal.Serial != serial || journal.Port is < 1024 or > 65535 || Keys.Any(k => !journal.Original.ContainsKey(k)))
            throw new InvalidDataException("代理恢复记录与设备不符。");
        var current = await ReadAsync(serial, token).ConfigureAwait(false);
        if (Keys.All(k => current[k] == journal.Original[k])) { File.Delete(path); return; }
        foreach (var key in Keys)
        {
            var managed = key switch { "http_proxy" => "127.0.0.1:" + journal.Port, "global_http_proxy_host" => "127.0.0.1", "global_http_proxy_port" => journal.Port.ToString(), _ => "" };
            // A prior interrupted restore may already have cleared individual keys.
            if (current[key] != journal.Original[key] && current[key] != managed && !IsCleared(key, current[key]))
                throw new InvalidOperationException("代理配置已被其他程序或用户修改；未覆盖这些修改，恢复记录已保留。");
        }
        // :0 explicitly tells Android's legacy observer to clear its live proxy;
        // simply deleting an absent original key need not clear the in-memory proxy.
        await PutAsync(serial, "http_proxy", ":0", token).ConfigureAwait(false);
        await Task.Delay(250, token).ConfigureAwait(false);
        foreach (var key in Keys.Where(k => k != "http_proxy").Append("http_proxy"))
        {
            if (journal.Original[key] is { } value) await PutAsync(serial, key, value, token).ConfigureAwait(false);
            else await adb(serial, ["shell", "settings", "delete", "global", key], token).ConfigureAwait(false);
        }
        await Task.Delay(250, token).ConfigureAwait(false);
        var restored = await ReadAsync(serial, token).ConfigureAwait(false);
        if (Keys.Any(k => restored[k] != journal.Original[k])) throw new IOException("代理恢复未通过设备回读验证，已保留恢复记录。");
        File.Delete(path);
    }

    private static bool IsCleared(string key, string? value) => string.IsNullOrEmpty(value) || (key == "http_proxy" && value is ":0" or "null" or ":-1") || (key == "global_http_proxy_port" && value is "0" or "-1");
    private static bool IsEmptyProxy(Dictionary<string, string?> values) => Keys.All(k => IsCleared(k, values[k]));
    private async Task<Dictionary<string, string?>> ReadAsync(string serial, CancellationToken token)
    {
        var output = await adb(serial, ["shell", "settings", "list", "global"], token).ConfigureAwait(false);
        if (!output.Split('\n').Any(line => line.IndexOf('=') > 0))
            throw new IOException("未能读取设备全局设置；没有修改代理，已有恢复记录将保留。");
        var values = Keys.ToDictionary(k => k, _ => (string?)null);
        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator > 0 && values.ContainsKey(line[..separator])) values[line[..separator]] = line[(separator + 1)..].TrimEnd('\r');
        }
        return values;
    }
    private Task<string> PutAsync(string serial, string key, string value, CancellationToken token) =>
        adb(serial, ["shell", "settings", "put", "global", key, "'" + value.Replace("'", "'\\''") + "'"], token);

    internal static async Task WriteDurableAsync<T>(string path, T value, CancellationToken token, bool overwrite = false)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, cancellationToken: token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
