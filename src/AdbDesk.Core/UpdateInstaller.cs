using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdbDesk.Core;

public sealed record PreparedUpdate(string PlanPath, string HelperExePath, string ReadyPath, string Nonce, string Version);
public sealed record UpdateInstallResult(bool Success, bool RolledBack, string Message, string TargetExePath, string LogPath);

/// <summary>Stages a signed portable update and commits it after the exact parent process exits.</summary>
public sealed class UpdateInstaller(string currentVersion, long currentCode = 0)
{
    public const string InstallationMarkerName = "AdbDesk.installation.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ExeName = "AdbDesk.exe";

    public async Task<PreparedUpdate> StageAsync(DownloadedUpdate update, string installationDir,
        int parentPid, long parentStartUtcTicks, CancellationToken cancellationToken = default)
    {
        var target = ValidateTarget(installationDir, update.Directory);
        ReadMarker(target, currentVersion);
        await ValidateInstalledVersionAsync(target, cancellationToken);
        using (BindParent(parentPid, parentStartUtcTicks, Path.Combine(target, ExeName))) { }
        ProbeWritable(target);
        var download = Path.GetFullPath(update.Directory);
        EnsureNoReparse(download);
        foreach (var path in new[] { update.ArchivePath, update.ManifestPath, update.SignaturePath })
        {
            RequireContained(download, path);
            EnsureNoReparse(path);
        }
        var manifestBytes = await ReadBoundedAsync(update.ManifestPath, UpdateManifestVerifier.MaxManifestBytes, cancellationToken);
        var signatureBytes = await ReadBoundedAsync(update.SignaturePath, 16384, cancellationToken);
        var manifest = UpdateManifestVerifier.Verify(manifestBytes, signatureBytes);
        if (manifest.Version != update.Version) throw new InvalidDataException("Downloaded version does not match its signed manifest.");
        await VerifyFileAsync(update.ArchivePath, manifest.Size, manifest.Sha256, cancellationToken);

        var work = Path.Combine(download, "install-" + Guid.NewGuid().ToString("N"));
        SafeCreateDirectory(work);
        var payload = Path.Combine(work, "payload");
        await ExtractPayloadAsync(update.ArchivePath, manifest, payload, cancellationToken);
        ReadMarker(payload, manifest.Version);
        var versionCode = await ValidatePayloadVersionAsync(manifest, payload, currentVersion, currentCode, cancellationToken);
        if (versionCode != update.VersionCode) throw new InvalidDataException("Downloaded version code does not match its signed payload.");
        await File.WriteAllBytesAsync(Path.Combine(work, "manifest.json"), manifestBytes, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(work, "manifest.sig"), signatureBytes, cancellationToken);
        var helper = Path.Combine(work, "helper");
        CopyCurrentRuntime(target, helper, cancellationToken);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var plan = new InstallPlan(1, target, currentVersion, manifest.Version, parentPid, parentStartUtcTicks, nonce, currentCode, versionCode);
        var planPath = Path.Combine(work, "plan.json");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, Json), cancellationToken);
        return new PreparedUpdate(planPath, Path.Combine(helper, ExeName), Path.Combine(work, "ready.json"), nonce, manifest.Version);
    }

    public async Task<UpdateInstallResult> ApplyAsync(string planPath, CancellationToken cancellationToken = default)
    {
        string targetExe = "", logPath = "";
        bool parentExited = false;
        Semaphore? installationGate = null;
        bool gateAcquired = false;
        try
        {
            var fullPlan = Path.GetFullPath(planPath);
            var work = Path.GetDirectoryName(fullPlan) ?? throw new InvalidDataException("Missing update workspace.");
            if (Path.GetFileName(fullPlan) != "plan.json" || !Path.GetFileName(work).StartsWith("install-", StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected update plan location.");
            EnsureNoReparse(work);
            EnsureNoReparse(fullPlan);
            ThrowIfAbandoned(fullPlan);
            var expectedHelper = Path.Combine(work, "helper", ExeName);
            if (!SamePath(Environment.ProcessPath ?? "", expectedHelper))
                throw new InvalidDataException("Updates must run in the staged helper process.");
            EnsureNoReparse(expectedHelper);
            var plan = JsonSerializer.Deserialize<InstallPlan>(await ReadBoundedAsync(fullPlan, 16384, cancellationToken), Json)
                ?? throw new InvalidDataException("Invalid update plan.");
            if (plan.SchemaVersion != 1 || plan.CurrentVersion != currentVersion || plan.CurrentCode != currentCode || currentCode < 0 || plan.Nonce is null ||
                plan.Nonce.Length != 64 || !plan.Nonce.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid update plan identity.");
            var target = ValidateTarget(plan.InstallationDirectory, work);
            ReadMarker(target, plan.CurrentVersion);
            await ValidateInstalledVersionAsync(target, cancellationToken);
            var targetId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())));
            installationGate = new Semaphore(1, 1, @"Local\AdbDesk.Update." + targetId);
            gateAcquired = installationGate.WaitOne(0);
            if (!gateAcquired) throw new IOException("Another updater is already preparing this installation.");
            var manifest = UpdateManifestVerifier.Verify(
                await ReadBoundedAsync(Path.Combine(work, "manifest.json"), UpdateManifestVerifier.MaxManifestBytes, cancellationToken),
                await ReadBoundedAsync(Path.Combine(work, "manifest.sig"), 16384, cancellationToken));
            if (manifest.Version != plan.Version) throw new InvalidDataException("Plan version does not match the signed update.");
            var payload = Path.Combine(work, "payload");
            await VerifyPayloadAsync(manifest, payload, cancellationToken);
            ReadMarker(payload, manifest.Version);
            if (await ValidatePayloadVersionAsync(manifest, payload, currentVersion, currentCode, cancellationToken) != plan.VersionCode)
                throw new InvalidDataException("Plan version code does not match the signed payload.");
            if (plan.ParentPid == Environment.ProcessId) throw new InvalidDataException("The helper cannot update its own installation.");
            using var parent = BindParent(plan.ParentPid, plan.ParentStartUtcTicks, Path.Combine(target, ExeName));
            // Acquiring the process handle above binds the wait to this process, even if its PID is later reused.
            targetExe = Path.Combine(target, ExeName);
            logPath = Path.Combine(work, "journal.jsonl");
            var readyPath = Path.Combine(work, "ready.json");
            EnsureNoReparse(readyPath);
            ThrowIfAbandoned(fullPlan);
            using (var stream = new FileStream(readyPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(stream, new { nonce = plan.Nonce, version = plan.Version }, Json);
                stream.Flush(true);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                var exit = parent.WaitForExitAsync(timeout.Token);
                while (!exit.IsCompleted)
                {
                    ThrowIfAbandoned(fullPlan);
                    if (!File.Exists(readyPath)) throw new OperationCanceledException("The update handoff was canceled.");
                    await Task.WhenAny(exit, Task.Delay(250, timeout.Token));
                    timeout.Token.ThrowIfCancellationRequested();
                }
                await exit;
            }
            catch (OperationCanceledException)
            {
                return new(false, false, "The original application did not exit in time; no installation files were changed.", "", logPath);
            }
            ThrowIfAbandoned(fullPlan);
            ValidateTarget(target, work);
            ReadMarker(target, plan.CurrentVersion);
            await ValidateInstalledVersionAsync(target, cancellationToken);
            if (await ValidatePayloadVersionAsync(manifest, payload, currentVersion, currentCode, cancellationToken) != plan.VersionCode)
                throw new InvalidDataException("Staged version code changed during the handoff.");
            parentExited = true;
            return await ApplyTransactionAsync(manifest, payload, target, Path.Combine(work, "backup"), logPath, cancellationToken);
        }
        catch (Exception exception)
        {
            return new(false, parentExited, "Update was not installed: " + exception.Message, parentExited ? targetExe : "", logPath);
        }
        finally
        {
            if (gateAcquired) installationGate!.Release();
            installationGate?.Dispose();
        }
    }

    internal static async Task ExtractPayloadAsync(string archivePath, UpdateManifest manifest, string payload, CancellationToken token)
    {
        EnsureNoReparse(archivePath);
        SafeCreateDirectory(payload);
        if (Directory.EnumerateFileSystemEntries(payload).Any()) throw new InvalidDataException("Payload directory must be empty.");
        var expected = ManifestFiles(manifest);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > UpdateManifestVerifier.MaxFiles) throw new InvalidDataException("Too many ZIP entries.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        // Validate every entry before writing any extracted file. Directory entries are deliberately not accepted.
        foreach (var entry in archive.Entries)
        {
            var path = UpdateManifestVerifier.ValidateFilePath(entry.FullName);
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode is not (0 or 0x8000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ZIP links and special files are not allowed.");
            if (!seen.Add(path) || !expected.TryGetValue(path, out var file) || entry.Length != file.Size)
                throw new InvalidDataException("ZIP contents differ from the signed file list.");
            total = checked(total + entry.Length);
            if (total > UpdateManifestVerifier.MaxExtractedBytes) throw new InvalidDataException("Expanded update is too large.");
        }
        if (seen.Count != expected.Count) throw new InvalidDataException("ZIP is missing signed files.");
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var relative = UpdateManifestVerifier.ValidateFilePath(entry.FullName);
            var destination = Child(payload, relative);
            SafeCreateDirectory(Path.GetDirectoryName(destination)!);
            EnsureNoReparse(destination);
            using var source = entry.Open();
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long count = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) != 0)
                {
                    count += read;
                    if (count > expected[relative].Size) throw new InvalidDataException("ZIP entry expands beyond its signed length.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            var file = expected[relative];
            await VerifyFileAsync(destination, file.Size, file.Sha256, token);
        }
    }

    internal static async Task<UpdateInstallResult> ApplyTransactionAsync(UpdateManifest manifest, string payload,
        string target, string backup, string journalPath, CancellationToken token, Action<string>? beforeReplace = null)
    {
        var changes = new List<Change>();
        var attempted = new List<Change>();
        FileStream? journal = null;
        try
        {
            EnsureNoReparse(target);
            EnsureNoReparse(backup);
            EnsureNoReparse(journalPath);
            await VerifyPayloadAsync(manifest, payload, token);
            var files = ManifestFiles(manifest).Values.OrderBy(file => string.Equals(file.Path, InstallationMarkerName, StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToArray();
            foreach (var file in files)
            {
                var destination = Child(target, file.Path);
                EnsureNoReparse(destination);
                if (Directory.Exists(destination)) throw new IOException("Update destination is a directory: " + file.Path);
                var existed = File.Exists(destination);
                if (existed && await MatchesAsync(destination, file.Size, file.Sha256, token)) continue;
                if (existed)
                {
                    // Share.None detects locked executables before any installation mutation.
                    using var probe = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                ProbeExistingParent(destination, target);
                changes.Add(new Change(file.Path, existed));
            }
            SafeCreateDirectory(backup);
            if (Directory.EnumerateFileSystemEntries(backup).Any()) throw new IOException("A previous update backup already exists; inspect its journal before retrying.");
            journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            Journal(journal, "prepared", new { version = manifest.Version, target, changes });
            // All affected originals are safely copied before the first replacement.
            foreach (var change in changes.Where(change => change.Existed))
            {
                token.ThrowIfCancellationRequested();
                var original = Child(target, change.Path);
                var saved = Child(backup, change.Path);
                SafeCreateDirectory(Path.GetDirectoryName(saved)!);
                EnsureNoReparse(original);
                EnsureNoReparse(saved);
                using var source = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(saved, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                change.OriginalLength = source.Length;
                change.OriginalAttributes = File.GetAttributes(original);
                change.OriginalLastWriteUtc = File.GetLastWriteTimeUtc(original);
                await source.CopyToAsync(output, token);
                output.Flush(true);
                source.Position = 0;
                change.OriginalSha256 = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
            }
            Journal(journal, "backup-complete", changes);
            foreach (var change in changes)
            {
                token.ThrowIfCancellationRequested();
                EnsureNoReparse(target);
                EnsureNoReparse(payload);
                var file = manifest.Files.Single(file => string.Equals(file.Path, change.Path, StringComparison.OrdinalIgnoreCase));
                await VerifyFileAsync(Child(payload, change.Path), file.Size, file.Sha256, token);
                beforeReplace?.Invoke(change.Path);
                // Durable intent comes first: recovery must include a move that succeeded just before a crash.
                Journal(journal, "replace-intent", change);
                AtomicCopy(Child(payload, change.Path), Child(target, change.Path), change.Existed);
                attempted.Add(change);
                Journal(journal, "replaced", change.Path);
            }
            Journal(journal, "committed", manifest.Version);
            return new(true, false, "Update installed successfully.", Path.Combine(target, ExeName), journalPath);
        }
        catch (Exception exception)
        {
            var rollbackErrors = new List<string>();
            foreach (var change in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    var destination = Child(target, change.Path);
                    EnsureNoReparse(destination);
                    if (change.Existed)
                    {
                        var saved = Child(backup, change.Path);
                        // A damaged backup must be reported as incomplete recovery, never restored silently.
                        await VerifyFileAsync(saved, change.OriginalLength, change.OriginalSha256!, CancellationToken.None);
                        AtomicCopy(saved, destination, true);
                        File.SetLastWriteTimeUtc(destination, change.OriginalLastWriteUtc);
                        File.SetAttributes(destination, change.OriginalAttributes);
                    }
                    else if (File.Exists(destination)) File.Delete(destination);
                    if (journal is not null) Journal(journal, "restored", change.Path);
                }
                catch (Exception rollbackException) { rollbackErrors.Add(change.Path + ": " + rollbackException.Message); }
            }
            var restored = rollbackErrors.Count == 0;
            var message = "Update failed: " + exception.Message + (restored
                ? (attempted.Count == 0 ? " No installation files were changed." : " Original files were restored.")
                : " Rollback is incomplete: " + string.Join("; ", rollbackErrors));
            try { if (journal is not null) Journal(journal, restored ? "rolled-back" : "rollback-incomplete", message); }
            catch (Exception logException) { message += " Journal write failed: " + logException.Message; }
            return new(false, restored, message, restored ? Path.Combine(target, ExeName) : "", journalPath);
        }
        finally { journal?.Dispose(); }
    }

    private static Dictionary<string, UpdateFile> ManifestFiles(UpdateManifest manifest)
    {
        if (manifest.Files.Count > UpdateManifestVerifier.MaxFiles) throw new InvalidDataException("Too many manifest files.");
        var files = new Dictionary<string, UpdateFile>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            var path = UpdateManifestVerifier.ValidateFilePath(file.Path);
            if (file.Size < 0 || !files.TryAdd(path, file)) throw new InvalidDataException("Invalid or duplicate manifest path.");
            total = checked(total + file.Size);
            if (total > UpdateManifestVerifier.MaxExtractedBytes) throw new InvalidDataException("Expanded update is too large.");
        }
        // A file cannot also be another file's parent directory (Windows comparison rules).
        foreach (var path in files.Keys)
        {
            var slash = path.LastIndexOf('/');
            while (slash >= 0)
            {
                if (files.ContainsKey(path[..slash])) throw new InvalidDataException("Conflicting manifest paths.");
                slash = path.LastIndexOf('/', slash - 1);
            }
        }
        return files;
    }

    private static async Task VerifyPayloadAsync(UpdateManifest manifest, string payload, CancellationToken token)
    {
        EnsureNoReparse(payload);
        foreach (var file in ManifestFiles(manifest).Values)
            await VerifyFileAsync(Child(payload, file.Path), file.Size, file.Sha256, token);
    }

    private static async Task VerifyFileAsync(string path, long size, string hash, CancellationToken token)
    {
        if (!await MatchesAsync(path, size, hash, token)) throw new InvalidDataException("Update file failed size/SHA-256 verification: " + Path.GetFileName(path));
    }

    private static async Task<bool> MatchesAsync(string path, long size, string hash, CancellationToken token)
    {
        EnsureNoReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.Length == size && string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), hash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken token)
    {
        EnsureNoReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 1 || stream.Length > limit) throw new InvalidDataException("Update metadata length is invalid.");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static Process BindParent(int pid, long startTicks, string targetExe)
    {
        if (pid <= 0 || startTicks <= 0) throw new InvalidDataException("Invalid original process identity.");
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            _ = process.Handle;
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startTicks ||
                !SamePath(process.MainModule?.FileName ?? "", targetExe)) throw new InvalidDataException("Original process identity does not match the installation.");
            return process;
        }
        catch { process?.Dispose(); throw; }
    }

    private static void ReadMarker(string directory, string version)
    {
        var markerPath = Path.Combine(directory, InstallationMarkerName);
        EnsureNoReparse(markerPath);
        if (!File.Exists(markerPath)) throw new InvalidDataException("This folder has no portable installation marker. Extract a complete current portable release into a separate folder before using in-app updates.");
        using var stream = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16384) throw new InvalidDataException("Invalid installation marker.");
        var marker = JsonSerializer.Deserialize<InstallationMarker>(stream, Json);
        if (marker is null || marker.SchemaVersion != 1 || marker.ApplicationId != "AdbDesk" || marker.Version != version)
            throw new InvalidDataException("This folder is not a matching portable AdbDesk installation.");
    }

    private static string ValidateTarget(string directory, string cache)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new InvalidDataException("Installation directory must be absolute.");
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        EnsureNoReparse(target);
        if (!Directory.Exists(target) || SamePath(target, Path.GetPathRoot(target)!) || IsContained(target, cache) || IsContained(cache, target))
            throw new InvalidDataException("Unsafe installation directory.");
        foreach (var special in new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
            if (!string.IsNullOrEmpty(special) && SamePath(target, special)) throw new InvalidDataException("Installation must have its own folder.");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && IsContained(windows, target)) throw new InvalidDataException("Windows directories cannot be update targets.");
        if (Directory.Exists(Path.Combine(target, ".git")) || File.Exists(Path.Combine(target, ".git")) ||
            Directory.Exists(Path.Combine(target, "src", "AdbDesk.App")) || File.Exists(Path.Combine(target, "AdbDesk.slnx")))
            throw new InvalidDataException("Source checkouts cannot be updated in place. Extract a portable release into a separate folder.");
        return target;
    }

    private static void CopyCurrentRuntime(string target, string helper, CancellationToken token)
    {
        SafeCreateDirectory(helper);
        foreach (var source in Directory.EnumerateFiles(target))
        {
            token.ThrowIfCancellationRequested();
            if (!new[] { ".exe", ".dll", ".json", ".dat", ".config" }.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase)) continue;
            CopyTrusted(source, Path.Combine(helper, Path.GetFileName(source)));
        }
        foreach (var directory in Directory.EnumerateDirectories(target))
        {
            var name = Path.GetFileName(directory);
            try { if (CultureInfo.GetCultureInfo(name).Name.Length == 0) continue; }
            catch (CultureNotFoundException) { continue; }
            EnsureNoReparse(directory);
            foreach (var source in Directory.EnumerateFiles(directory, "*.resources.dll"))
            {
                token.ThrowIfCancellationRequested();
                var destination = Path.Combine(helper, name, Path.GetFileName(source));
                SafeCreateDirectory(Path.GetDirectoryName(destination)!);
                CopyTrusted(source, destination);
            }
        }
        foreach (var required in new[] { ExeName, "AdbDesk.dll", "AdbDesk.Core.dll", "AdbDesk.deps.json", "AdbDesk.runtimeconfig.json", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll" })
            if (!File.Exists(Path.Combine(helper, required))) throw new InvalidDataException("Only the self-contained portable distribution supports in-app updates: missing " + required);
    }

    private static void CopyTrusted(string source, string destination)
    {
        EnsureNoReparse(source);
        EnsureNoReparse(destination);
        File.Copy(source, destination, false);
    }

    private static void AtomicCopy(string source, string destination, bool overwrite)
    {
        EnsureNoReparse(source);
        SafeCreateDirectory(Path.GetDirectoryName(destination)!);
        EnsureNoReparse(destination);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".adbdesk-update-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            EnsureNoReparse(destination);
            EnsureNoReparse(temporary);
            File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) { EnsureNoReparse(temporary); File.Delete(temporary); } }
    }

    private static void ProbeExistingParent(string destination, string target)
    {
        var parent = Path.GetDirectoryName(destination)!;
        while (!Directory.Exists(parent))
        {
            if (File.Exists(parent)) throw new IOException("A file blocks the update path: " + parent);
            parent = Path.GetDirectoryName(parent) ?? throw new IOException("No existing update parent.");
        }
        if (!IsContained(target, parent)) throw new InvalidDataException("Update path escaped installation.");
        ProbeWritable(parent);
    }

    private static void ProbeWritable(string directory)
    {
        EnsureNoReparse(directory);
        var probe = Path.Combine(directory, ".adbdesk-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        stream.WriteByte(0);
        stream.Flush(true);
    }

    private static void Journal(FileStream stream, string action, object detail)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { timeUtc = DateTime.UtcNow, action, detail }, Json) + "\n");
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static void ThrowIfAbandoned(string planPath)
    {
        var cancel = planPath + ".cancel";
        EnsureNoReparse(cancel);
        if (File.Exists(cancel)) throw new OperationCanceledException("The original application canceled the update handoff; no installation files were changed.");
    }

    private static void RequireNewer(string version, string current)
    {
        if (UpdateVersion.Parse(version).CompareTo(UpdateVersion.Parse(current)) <= 0)
            throw new InvalidDataException("The signed update must be newer than the installed version.");
    }

    internal async Task ValidateInstalledVersionAsync(string directory, CancellationToken token)
    {
        if (currentCode < 0) throw new InvalidDataException("Invalid installed version code.");
        if (currentCode == 0) return;
        var bytes = await ReadBoundedAsync(Path.Combine(directory, UpdateVersionMetadata.FileName), UpdateVersionMetadata.MaxBytes, token);
        if (UpdateVersionMetadata.Parse(bytes, currentVersion).VersionCode != currentCode)
            throw new InvalidDataException("Installation version code does not match the running application.");
    }

    // Shared by staging and the helper, including a second check after the parent exits.
    internal static async Task<long> ValidatePayloadVersionAsync(UpdateManifest manifest, string payload, string installedVersion, long installedCode, CancellationToken token)
    {
        if (installedCode < 0) throw new InvalidDataException("Invalid installed version code.");
        long code = 0;
        if (UpdateVersionMetadata.FindFile(manifest) is not null)
        {
            var bytes = await ReadBoundedAsync(Path.Combine(payload, UpdateVersionMetadata.FileName), UpdateVersionMetadata.MaxBytes, token);
            code = UpdateVersionMetadata.Verify(bytes, manifest).VersionCode;
        }
        if (installedCode > 0)
        {
            if (code <= installedCode) throw new InvalidDataException("The signed update version code must be newer than the installed version code.");
        }
        else RequireNewer(manifest.Version, installedVersion);
        return code;
    }

    private static string Child(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, UpdateManifestVerifier.ValidateFilePath(relative).Replace('/', Path.DirectorySeparatorChar)));
        RequireContained(root, path);
        return path;
    }
    private static void RequireContained(string root, string path)
    {
        if (SamePath(root, path) || !IsContained(root, path)) throw new InvalidDataException("Update path is outside its workspace.");
    }
    private static bool IsContained(string root, string path) => SamePath(root, path) || Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool SamePath(string left, string right) => !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private static void SafeCreateDirectory(string path) { EnsureNoReparse(path); Directory.CreateDirectory(path); EnsureNoReparse(path); }
    private static void EnsureNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Update paths cannot contain links or junctions: " + current); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private sealed record InstallPlan(int SchemaVersion, string InstallationDirectory, string CurrentVersion, string Version,
        int ParentPid, long ParentStartUtcTicks, string Nonce, long CurrentCode = 0, long VersionCode = 0);
    private sealed record InstallationMarker(int SchemaVersion, string ApplicationId, string Version);
    private sealed record Change(string Path, bool Existed)
    {
        public long OriginalLength { get; set; }
        public string? OriginalSha256 { get; set; }
        public DateTime OriginalLastWriteUtc { get; set; }
        public FileAttributes OriginalAttributes { get; set; }
    }
}
