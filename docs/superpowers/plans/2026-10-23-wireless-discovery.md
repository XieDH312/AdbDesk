# Wireless ADB Discovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Discover current wireless ADB endpoints and connect without retyping changed ports.

**Architecture:** Reuse ADB mDNS output, exact wireless transport matching, and existing pairing/connect/disconnect services. A modeless WPF window performs bounded, event-triggered refresh and exposes verified connection callbacks.

**Tech Stack:** C# / .NET 10 / WPF / existing ADB CLI.

**Spec:** docs/superpowers/specs/2026-10-23-wireless-discovery-design.md

## Global Constraints
- Existing branch codex/adbdesk-initial; preserve six preexisting untracked plans. No commits or releases requested this turn.
- No polling, subnet scan, VPN changes, or pairing-port connect attempts.
- Windows application-control restriction: build tests and application only; do not execute generated binaries or ADB commands. Do not claim runtime test success.

## Review Focus
- Same IP or USB identity must never mark a wireless service connected.
- Changed ports and stale list selections must be refreshed before an operation.
- Pairing endpoint and stale Wi-Fi override must not target the wrong service.
- Closing/reopening or restarting ADB must not apply late UI results or kill the shared server.
- Corrupt/oversized name caches and discovery errors must remain recoverable.

## Task 1: Core discovery and regression cases
- [x] Add WirelessDiscoveryTests for service types, malformed addresses, exact matching, changed port, bounded name cache and cancellation before implementation.
- [x] Extend MdnsServices with legacy type; implement WirelessDiscoveryService and name store.
- [x] Interface: WirelessServiceEntry(Name, Type, Endpoint, DeviceInfo? Device, DisplayName, IsHistoricalName), IsPairing/IsConnected; WirelessDiscoverySnapshot(Services, Warning); WirelessDiscoveryService(ToolPaths, cachePath).DiscoverAsync(token).
- [x] Build tests; execution is restricted by local application control.

## Task 2: Discovery window
- [x] Add WirelessDiscoveryWindow.xaml/.cs with cards, state/actions, empty/error/cancel states and no timer.
- [x] Constructor: (WirelessDiscoveryService, PairingService, AdbService, Func<string,Task> connected, Action<string> openPairing); IsBusy and RefreshAfterPairingAsync().
- [x] Re-resolve selected service on action; verified connect invokes callback; pairing only prefills pairing dialog.

## Task 3: Integration and verification
- [x] Add responsive top entry, single owned discovery window and cleanup/restart guards.
- [x] Add safe pairing prefill and verified-connected event; refresh/history follow existing conventions.
- [x] Register tests, document usage and manual device checks.
- [x] Build full Release solution with .tools/dotnet/dotnet.exe build AdbDesk.slnx -c Release --no-restore. Inspect diff and perform fresh independent review.
- [x] Report compile result separately from pending physical-device and GUI checks.

## Verification record
- Full Release solution build succeeded with 0 warnings and 0 errors, including all regression projects. Test binaries were compiled, not executed.
- Fresh review identified partial-pairing refresh, network-shaped USB serials, and ADB process-tree cancellation; all three were corrected and covered by source review / added regression cases.
- Additional event refreshes are coalesced while busy; explicit cancel clears pending refresh.
- Existing six untracked plans preserved; no Git commit, push or remote release performed.
- Physical phone and GUI verification remains manual per docs/wireless-discovery.md.
