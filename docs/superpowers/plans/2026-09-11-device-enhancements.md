# AdbDesk device enhancements

User approved three changes and clarified single-window means one AdbDesk main instance, independent phone mirror windows remain.

## Scope and contracts
- Keep changes in existing repo, preserve all user work. No desktop/phone automation: user will test manually.
- Task A (controller): named mutex + local same-user pipe coordinates one main instance across new builds; second launch notifies/activates the existing owned window and exits. Remove automatic StartupUri to check mutex before constructing MainWindow. Test using isolated names and real child processes, including rapid secondary launches and owner exit/reacquisition. No ADB needed in instance test harness.
- Task B (Core worker): device model/name/Android version enrichment, authoritative USB detection, cached/bounded read-only probes. Explicit-target USB-to-TCPIP 5555 enable + Wi-Fi IPv4 discovery + bounded connect/readiness check. Disable uses targeted adb usb, not host disconnect alone. Both actions require a currently authorized physical USB transport, rechecked at execution. No system Wi-Fi radio toggle, no TLS pairing implementation, no global adb restart or disconnect-all.
- Task C (UI worker): device name/model/OS/transport labels; USB-only wireless actions, busy state, durable result message and clear indication when TCP mode enabled but host connection fails. Explain reconnect may interrupt mirrors; do not switch with active install tasks. Live operations occur only when user presses action.
- Task D: independent review, regression/build/publish, update manual tests and hand off final EXE.

## Frozen Core contract additions
```csharp
public enum DeviceTransport { Unknown, Usb, Network, Emulator }
// existing DeviceInfo positional constructor unchanged:
public string DeviceName { get; init; } = "";
public string AndroidVersion { get; init; } = "";
public DeviceTransport Transport { get; init; } = DeviceTransport.Unknown;
public int? WirelessPort { get; init; }
public string DetailsError { get; init; } = "";
public record WirelessResult(bool ModeEnabled, bool Connected, string Endpoint, string Message)
{
    public bool Succeeded { get; init; } = true;
}
// AdbService new method, existing GetDevicesAsync enriches automatically:
public Task<WirelessResult> SetWirelessAsync(DeviceInfo device, bool enabled, CancellationToken cancellationToken = default);
```
Details can be unavailable without hiding devices. WirelessPort reflects observed TCP mode, not TLS Wireless Debugging. State-changing results invalidate the information cache. After a returned TCP-enabled/connect-failed result user can keep USB and retry/close; never report connected based only on adb connect exit code.

## Task ownership / progress
- [x] A: implementation complete; runtime tests blocked by Smart App Control (0x800711C7), manual acceptance pending
- [x] B: metadata and USB wireless service complete; Core harness 13/13 passed
- [x] C: device labels, USB wireless controls, busy/install guards and result handling complete
- [x] D: solution/build script/docs updated; independent static review completed with no remaining substantive findings; portable app published at dist/AdbDesk-win-x64-20260911-154900; manual verification pending as documented

Tests: existing queue/mirror suite plus rich metadata parsing, unauthorized probes skipped, cache behavior, USB/network classification, only explicit target commands, no TCP mutation when no Wi-Fi/USB, enable connection-success/error response handling, close returns USB, isolated cross-process singleton exclusion/activation/reacquisition. Package via scripts/build.ps1 after these checks pass.

Verification adjustment: Windows Smart App Control blocked the isolated instance test binaries. Do not disable or evade the policy. Keep the normal build script test gates, publish a manual-test artifact with dotnet publish, and document that singleton runtime/GUI/phone verification remains pending. Overall Release compilation passed with zero warnings/errors.
