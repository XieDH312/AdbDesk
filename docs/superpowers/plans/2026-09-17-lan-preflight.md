# LAN status and connection preflight

User approved: show/copy local IPv4 in the footer; live refresh; show adapters in a menu and mask/gateway in tooltips. Warn on different subnets with Cancel / Connect anyway, never reject routed networks categorically. Cover direct connect, QR after discovery, code pairing, and USB TCP/IP enable before mutation.

1. Add Core LocalNetwork snapshot discovery and pure IPv4/mask comparison. Prefer active physical Wi-Fi/Ethernet with gateways, list additional adapters separately; never silently use WSL as the main LAN address. Unknown state stays unknown.
2. Add optional cancellable endpoint confirmation callbacks to pairing and USB enable services. Validate addresses before confirmation; declining submits no pair/connect/tcpip. QR expiry/cancellation remain enforced after confirmation.
3. Add footer/menu and a reusable owned WPF warning dialog, with all UI work dispatched to the owner thread. Updated per user follow-up: read once at startup; refresh on NetworkChange address/availability events with a one-shot 300ms debounce, and on explicit copy/menu/preflight actions. Unsubscribe static events on close; no periodic IP polling. Keep dialogs bounded and cancel when the operation expires or the app closes.
4. Add regression cases for /24, /16, /23 and secondary adapters, unknown networks, and declined/canceled confirmation at mutation boundaries. Run Release build; existing Smart App Control runtime-test block must not be bypassed.
5. Review diffs, update manual verification, publish a new timestamped portable package. Preserve all prior uncommitted changes; no GUI/phone mutations or Git push.
