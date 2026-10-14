# Third-party components

AdbDesk uses the official, unmodified scrcpy v4.1 Windows x64 distribution:
https://github.com/Genymobile/scrcpy/releases/tag/v4.1

Its upstream LICENSE.txt is retained at tools/scrcpy/LICENSE.txt. scrcpy is Apache-2.0.
Additional upstream notices are retained in tools/scrcpy/licenses (source copies in licenses/).
The distribution also includes ADB and its USB libraries, SDL3, FFmpeg libraries, and libusb.
Upstream source and license information:

- scrcpy: https://github.com/Genymobile/scrcpy/tree/v4.1
- ADB: https://android.googlesource.com/platform/packages/modules/adb/
- SDL: https://github.com/libsdl-org/SDL (zlib)
- FFmpeg: https://ffmpeg.org/legal.html (license depends on upstream build configuration)
- libusb: https://github.com/libusb/libusb (LGPL-2.1)
- .NET: https://github.com/dotnet/runtime ; Microsoft .NET release license and third-party notices are included by self-contained publishing.

Tool archive SHA256: 5b12172b3264b2889f4583ee64752ce832e29bc8b1089dca81093459697165db.
This project does not incorporate binaries or source from the aztpfree reference application.

The wireless pairing dialog renders QR codes locally using QRCoder 1.8.0 (MIT):
https://github.com/Shane32/QRCoder/tree/v1.8.0
The full license is retained at licenses/QRCoder-LICENSE.txt and included in the portable application.
ZXing.Net 0.16.11 is used only by the QR image regression tests and is not distributed with the application.

The optional network capture engine is the unmodified mitmdump.exe from mitmproxy 12.2.3 (MIT):
https://github.com/mitmproxy/mitmproxy/tree/v12.2.3
Official archive: https://downloads.mitmproxy.org/12.2.3/mitmproxy-12.2.3-windows-x86_64.zip
SHA256 of the downloaded archive: 04a01ea95ae96df75058a893e774957d294e69012dab1f4e256ce2b0c6725483.
The checksum is pinned for reproducible downloads; no independent signature verification is claimed.
License: licenses/mitmproxy-LICENSE.txt. The portable engine includes upstream Python and dependency components. Its embedded upstream license files are retained in the unmodified executable;
source dependencies and license metadata are also available through the upstream release source and dependency manifest.
AdbDesk's addon communicates with this separate process and does not modify its executable.
