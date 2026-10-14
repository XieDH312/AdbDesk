using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using AdbDesk.Core;

internal static class UpdateInstallTests
{
    // These assertions catch partial commits, deletion of user files, and marker publication before commit.
    public static async Task TransactionAsync()
    {
        using var fixture = new Fixture();
        fixture.Old("AdbDesk.exe", "old exe");
        fixture.Old("tools/adb.exe", "shared adb");
        fixture.Old("user/preferences.json", "keep preferences");
        fixture.Old(UpdateInstaller.InstallationMarkerName, "old marker");
        var manifest = fixture.Payload(("AdbDesk.exe", "new exe"), ("new.dll", "new dll"),
            ("tools/adb.exe", "shared adb"), (UpdateInstaller.InstallationMarkerName, "new marker"));
        using var runningAdb = new FileStream(Path.Combine(fixture.Target, "tools/adb.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await UpdateInstaller.ApplyTransactionAsync(manifest, fixture.PayloadDirectory, fixture.Target,
            fixture.Backup, fixture.Log, CancellationToken.None);
        Check(result.Success, result.Message);
        Check(fixture.Read("AdbDesk.exe") == "new exe", "updated executable");
        Check(fixture.Read("new.dll") == "new dll", "added file");
        Check(fixture.Read("user/preferences.json") == "keep preferences", "preserves unrelated user data");
        Check(fixture.Read(UpdateInstaller.InstallationMarkerName) == "new marker", "publishes marker");
        Check(!File.Exists(Path.Combine(fixture.Backup, "tools/adb.exe")), "unchanged running adb is not replaced or backed up");
    }

    public static async Task RollbackAsync()
    {
        using var fixture = new Fixture();
        fixture.Old("AdbDesk.exe", "old exe");
        fixture.Old(UpdateInstaller.InstallationMarkerName, "old marker");
        var manifest = fixture.Payload(("AdbDesk.exe", "new exe"), ("new.dll", "new dll"),
            (UpdateInstaller.InstallationMarkerName, "new marker"));
        var result = await UpdateInstaller.ApplyTransactionAsync(manifest, fixture.PayloadDirectory, fixture.Target,
            fixture.Backup, fixture.Log, CancellationToken.None, path =>
            {
                if (path == UpdateInstaller.InstallationMarkerName) throw new IOException("Injected replacement failure");
            });
        Check(!result.Success && result.RolledBack, result.Message);
        Check(fixture.Read("AdbDesk.exe") == "old exe", "restores original executable");
        Check(!File.Exists(Path.Combine(fixture.Target, "new.dll")), "removes only newly created update file");
        Check(fixture.Read(UpdateInstaller.InstallationMarkerName) == "old marker", "marker stays original after rollback");
        Check(File.Exists(fixture.Log) && File.Exists(Path.Combine(fixture.Backup, "AdbDesk.exe")), "retains diagnostic journal and backup");
    }

    public static async Task LockedAndCorruptAsync()
    {
        using var fixture = new Fixture();
        fixture.Old("AdbDesk.exe", "old exe");
        fixture.Old("locked.dll", "old dll");
        var manifest = fixture.Payload(("AdbDesk.exe", "new exe"), ("locked.dll", "new dll"));
        using (var locked = new FileStream(Path.Combine(fixture.Target, "locked.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await UpdateInstaller.ApplyTransactionAsync(manifest, fixture.PayloadDirectory, fixture.Target,
                fixture.Backup, fixture.Log, CancellationToken.None);
            Check(!result.Success && result.RolledBack, "locked changed file is rejected before commit");
            Check(fixture.Read("AdbDesk.exe") == "old exe", "preflight does not partially change files");
        }
        File.WriteAllText(Path.Combine(fixture.PayloadDirectory, "AdbDesk.exe"), "corrupted staged bytes");
        var corrupted = await UpdateInstaller.ApplyTransactionAsync(manifest, fixture.PayloadDirectory, fixture.Target,
            fixture.Backup, fixture.Log, CancellationToken.None);
        Check(!corrupted.Success && fixture.Read("AdbDesk.exe") == "old exe", "revalidates staged hashes before mutation");
    }

    public static async Task IncompleteRollbackAsync()
    {
        using var fixture = new Fixture();
        fixture.Old("AdbDesk.exe", "old exe");
        fixture.Old(UpdateInstaller.InstallationMarkerName, "old marker");
        var manifest = fixture.Payload(("AdbDesk.exe", "new exe"), (UpdateInstaller.InstallationMarkerName, "new marker"));
        var result = await UpdateInstaller.ApplyTransactionAsync(manifest, fixture.PayloadDirectory, fixture.Target,
            fixture.Backup, fixture.Log, CancellationToken.None, path =>
            {
                if (path != UpdateInstaller.InstallationMarkerName) return;
                File.Delete(Path.Combine(fixture.Backup, "AdbDesk.exe"));
                throw new IOException("Injected replacement and recovery failure");
            });
        Check(!result.Success && !result.RolledBack && result.TargetExePath.Length == 0,
            "incomplete rollback must not claim success or offer a mixed installation for relaunch");
        Check(result.Message.Contains("Rollback is incomplete", StringComparison.Ordinal), "explicit recovery failure");
        Check(File.ReadAllText(fixture.Log).Contains("rollback-incomplete", StringComparison.Ordinal), "durable recovery diagnostic");
    }

    public static async Task ZipValidationAsync()
    {
        foreach (var badName in new[] { "../escape.dll", "/absolute.dll", "C:/absolute.dll", "file:stream", "NUL.dll", "dir/../escape.dll", "trailing. /bad.dll" })
        {
            using var fixture = new Fixture();
            var manifest = fixture.Payload(("AdbDesk.exe", "safe"));
            var zip = fixture.Zip((badName, "safe"));
            await RejectAsync(() => UpdateInstaller.ExtractPayloadAsync(zip, manifest, Path.Combine(fixture.Root, "extract"), CancellationToken.None));
        }
        using (var fixture = new Fixture())
        {
            var manifest = fixture.Payload(("AdbDesk.exe", "safe"));
            var zip = fixture.Zip(("AdbDesk.exe", "safe"), ("adbdesk.EXE", "safe"));
            await RejectAsync(() => UpdateInstaller.ExtractPayloadAsync(zip, manifest, Path.Combine(fixture.Root, "extract"), CancellationToken.None));
        }
        using (var fixture = new Fixture())
        {
            var manifest = fixture.Payload(("AdbDesk.exe", "safe"));
            var zip = fixture.Zip(("AdbDesk.exe", "safe"));
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
                archive.Entries[0].ExternalAttributes = unchecked((int)0xA1FF0000);
            await RejectAsync(() => UpdateInstaller.ExtractPayloadAsync(zip, manifest, Path.Combine(fixture.Root, "extract"), CancellationToken.None));
        }
        using (var fixture = new Fixture())
        {
            var manifest = fixture.Payload(("AdbDesk.exe", "safe"));
            var zip = fixture.Zip(("AdbDesk.exe", "evil"));
            await RejectAsync(() => UpdateInstaller.ExtractPayloadAsync(zip, manifest, Path.Combine(fixture.Root, "extract"), CancellationToken.None));
        }
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); } catch (InvalidDataException) { return; } catch (ArgumentException) { return; }
        throw new Exception("Expected unsafe update to be rejected.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AdbDesk-update-tests-" + Guid.NewGuid().ToString("N"));
        public string Target => Path.Combine(Root, "installation");
        public string PayloadDirectory => Path.Combine(Root, "payload");
        public string Backup => Path.Combine(Root, "backup");
        public string Log => Path.Combine(Root, "journal.jsonl");
        public Fixture() { Directory.CreateDirectory(Target); Directory.CreateDirectory(PayloadDirectory); }
        public void Old(string path, string value) => Write(Target, path, value);
        public string Read(string path) => File.ReadAllText(Path.Combine(Target, path));
        public UpdateManifest Payload(params (string Path, string Value)[] files)
        {
            foreach (var file in files) Write(PayloadDirectory, file.Path, file.Value);
            return new UpdateManifest(1, "AdbDesk", "1.0.0-beta.2", "AdbDesk.zip", 1, new string('0', 64),
                files.Select(file => new UpdateFile(file.Path, Encoding.UTF8.GetByteCount(file.Value),
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Value))))).ToArray());
        }
        public string Zip(params (string Path, string Value)[] files)
        {
            var path = Path.Combine(Root, "test.zip");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                using var stream = zip.CreateEntry(file.Path).Open();
                stream.Write(Encoding.UTF8.GetBytes(file.Value));
            }
            return path;
        }
        private static void Write(string root, string path, string value)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, value, new UTF8Encoding(false));
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
