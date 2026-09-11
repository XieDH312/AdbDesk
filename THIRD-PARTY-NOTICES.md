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
