namespace AdbDesk.Core;

public sealed record UpdateFile(string Path, long Size, string Sha256);
public sealed record UpdateManifest(int SchemaVersion, string ApplicationId, string Version, string AssetName, long Size, string Sha256, IReadOnlyList<UpdateFile> Files);
public sealed record AvailableUpdate(UpdateManifest Manifest, byte[] ManifestBytes, byte[] SignatureBytes, string Notes, Uri PageUrl, Uri PackageUrl, long VersionCode = 0, byte[]? VersionMetadataBytes = null);
public sealed record DownloadedUpdate(string Directory, string ArchivePath, string ManifestPath, string SignaturePath, string Version, long VersionCode = 0);
public sealed record UpdateProgress(long Received, long? Total, string Message);
