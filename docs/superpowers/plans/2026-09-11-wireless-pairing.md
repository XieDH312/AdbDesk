# Wireless QR and pairing-code connections implementation plan

> For agentic workers: use superpowers:subagent-driven-development for implementation and independent review.

Goal: Add the two Android 11+ wireless-debugging connection methods explicitly approved by the user, reusing the existing device list, mirroring and installation features.

Architecture: A new Core PairingService orchestrates the bundled ADB pairing and mDNS commands. One owned WPF dialog contains QR and six-digit pairing-code tabs; two main-window buttons open the respective tab. QRCoder 1.8.0 renders QR images locally. No phone app or Android Studio installation is required.

Spec: The preceding user-approved flow: desktop displays QR, phone scans from Wireless debugging; alternatively enter the phone's pairing address and six-digit code. Pair then connect automatically and refresh device list. Manual connection-address fallback covers unavailable mDNS.

## Global constraints

- Work in D:/work/xdh/AdbDesk on existing codex/adbdesk-initial branch; preserve previous functionality and unrelated work. Baseline 34cd03f.
- User will test desktop and phones. No GUI automation or real pairing/connect/install commands during development.
- Read-only source/docs/build/fake-process tests are authorized. Never disable or bypass Smart App Control; if tests are blocked, document that limitation and still compile/publish the manual-test artifact.
- Android 11+ Wireless debugging uses TLS and dynamic pairing/connect ports; do not use tcpip/usb/5555 for these flows.
- QR payload follows AOSP WIFI:T:ADB;S:studio-<random>;P:<random>;;. Use cryptographic randomness and QR-safe alphanumeric secrets. Expire a QR session after two minutes. Never persist/log QR secrets or pairing codes.
- Pairing success is distinct from connected-and-authorized state. Exit code alone does not establish success. Verify target get-state=device before Connected=true.
- No arbitrary discovery-driven bulk pairing: QR matches only the current random service name. Code pairing targets the user's validated endpoint. Auto-connect only the paired device; provide explicit connect-address fallback if discovery cannot identify it.
- Cancellation/refresh/tab switch/close stop discovery, future submissions and owned CLI waits. ADB server executes already-submitted pairing independently and may still finish it after the local client closes; UI/docs must state this boundary. Never kill the global ADB server to cancel pairing.

## Frozen interfaces (Core owns)

```csharp
public sealed class QrPairingSession
{
    public string ServiceName { get; }
    public string Payload { get; }
    public DateTimeOffset ExpiresAt { get; }
    // secret internal/private; ToString must not expose credentials
}
public record PairingResult(bool Paired, bool Connected, string Endpoint, string Message);
public sealed class PairingService
{
    public PairingService(ToolPaths paths);
    public static QrPairingSession CreateQrSession();
    public Task<PairingResult> PairWithQrAsync(QrPairingSession session, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    public Task<PairingResult> PairWithCodeAsync(string pairingEndpoint, string code, string? connectionEndpoint = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
    public Task<PairingResult> ConnectAsync(string connectionEndpoint, CancellationToken cancellationToken = default);
}
```

## Task A - pairing protocol and tests (Core worker)

Files: new src/AdbDesk.Core/PairingService.cs and focused pairing helper/model files; src/AdbDesk.Core/ProcessRunner.cs only if needed for stdin secrets; tests/AdbDesk.Core.Tests/Program.cs and optional new files under same harness.

- [x] Write meaningful failing fake-ADB tests for QR payload uniqueness/expiry, mDNS exact match parsing, IPv4:port and code validation, code/QR pair and target connect, differing ports, pair-success/connect-failure, cancellation, no unrelated-device selection, credential redaction.
- [x] Implement using official AOSP protocol. Prefer stdin for pairing secret so it is not exposed in process arguments. If extending ProcessRunner, preserve current callers and bounds for stdin/output/cancellation.
- [x] Run Release Core harness. Report evidence and changed files; no commit or GUI/device operations. Only this worker builds/runs Core during implementation.

## Task B - pairing dialog and entry points (UI worker)

Files: new src/AdbDesk.App/PairingWindow.xaml/.cs and QR renderer helper as needed; MainWindow.xaml/.cs; AdbDesk.App.csproj for QRCoder pinned 1.8.0 reference.

- [x] Add two main-header buttons: 扫码连接 / 配对码连接. Owned dialog with matching initial tab, at most one dialog per main window.
- [x] Show clear Android 11+, same-LAN and phone scanner instructions. QR is black on white with quiet zone; local PNG byte renderer, no secret written to files/logs.
- [x] QR refresh/expiry and tab switch cancel previous operation; no stale callback/UI updates. Code fields: pairing IPv4:port, six-digit code, optional separate connection IPv4:port. Explain where each is shown on phone and that ports differ.
- [x] Show progress, cancellation, partial paired-but-not-connected result, and manual connection fallback. Automatically refresh the main device list on return/connection success. Do not falsely report online after pair only.
- [x] Ensure small-window layout and lifecycle by source review; compile only on controller request to avoid concurrent builds. No GUI automation and no commit.

## Task C - independent review, verification, portable package (controller)

- [x] Review diff and focused tests, fix material findings through owning worker, and re-review fixes.
- [x] Update README, verification notes and third-party QRCoder license; copy license in published output.
- [x] Run headless QR PNG decode test from tests/AdbDesk.Qr.Tests (same renderer source, ZXing.Net test-only decoder); no desktop windows/camera or actual credentials.
- [x] Build whole solution Release and inspect Core test results. Do not retry unchanged policy-blocked singleton harness.
- [x] Publish a new timestamped self-contained win-x64 folder/ZIP without overwriting old releases, verify contents and hand over concise manual test steps.

Protocol source: https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/docs/dev/adb_wifi.md
QR renderer source: https://github.com/Shane32/QRCoder

Completion: Core 18/18 and headless QR 1/1 (4 decodes) passed; whole Release build 0 warnings/errors. Independent protocol/Core/UI review complete. Initial small-screen overflow corrected and re-reviewed. Published to dist/AdbDesk-win-x64-20260911-162200. User will perform all desktop/phone validation. Controller completed final dialog integration; source ownership was handed over before those edits. Older singleton runtime tests remain blocked by system policy and were not retried.
