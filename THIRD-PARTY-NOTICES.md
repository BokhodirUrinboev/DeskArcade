# Third-Party Notices

Desk Arcade is released under the [MIT License](LICENSE). It is built on the open-source components
listed below. Each component is the property of its respective authors and is used under the license
shown. This file covers everything redistributed in the Windows installers, the Ubuntu `.deb`
packages, the AppImages, the Flatpak build and the macOS app bundle.

Desk Arcade does not bundle any fonts, images, audio files or other third-party media. All sounds
are synthesized at runtime, all artwork is drawn in code, and text uses fonts already installed on
the system.

## Summary

| Component | Version | License | Shipped in | Project |
|---|---|---|---|---|
| Avalonia UI (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Simple` and the platform packages they bring in: `Avalonia.Skia`, `Avalonia.Win32`, `Avalonia.X11`, `Avalonia.FreeDesktop`, `Avalonia.Native`, `Avalonia.Remote.Protocol`) | 11.3.22 | MIT | All builds | <https://github.com/AvaloniaUI/Avalonia> |
| Avalonia.Angle.Windows.Natives (ANGLE) | 2.1.25547.20250602 | BSD-3-Clause | Windows | <https://github.com/AvaloniaUI/angle> |
| SkiaSharp | 2.88.9 | MIT | All builds | <https://github.com/mono/SkiaSharp> |
| Skia (native library inside SkiaSharp) | — | BSD-3-Clause | All builds | <https://skia.org> |
| HarfBuzzSharp | 8.3.1.1 | MIT | All builds | <https://github.com/mono/SkiaSharp> |
| HarfBuzz (native library inside HarfBuzzSharp) | 8.3.1 | "Old MIT" | All builds | <https://github.com/harfbuzz/harfbuzz> |
| MicroCom.Runtime | 0.11.0 | MIT | All builds | <https://github.com/kekekeks/MicroCom> |
| Tmds.DBus.Protocol | 0.21.3 | MIT | All builds (Linux tray, and global shortcuts on Wayland) | <https://github.com/tmds/Tmds.DBus> |
| [AppImage type2 runtime](https://github.com/AppImage/type2-runtime) | 20251108 | MIT | Inside the `.AppImage` files only | <https://github.com/AppImage/type2-runtime> |
| .NET Runtime | 10.0 | MIT | Ubuntu `.deb` and standalone Windows builds | <https://github.com/dotnet/runtime> |

`Avalonia.BuildServices` (MIT) is used only while compiling and is not redistributed. The unit tests use
[xUnit.net](https://github.com/xunit/xunit) (Apache-2.0) and Microsoft.NET.Test.Sdk (MIT); both are
test-only and ship in no package.

### Used at runtime, not redistributed

These are loaded from the operating system when present. Desk Arcade does not ship them.

| Component | License | Purpose |
|---|---|---|
| Xlib / libXext (X.Org) | MIT/X11 | Overlay input shape, window list, global hotkeys on Linux |
| libpulse (PulseAudio / PipeWire compatibility) | LGPL-2.1-or-later | Sound on Linux, loaded dynamically if installed |
| Windows `user32`, `winmm` | Part of Windows | Overlay, hotkeys and sound on Windows |
| macOS AppKit, CoreGraphics, CoreFoundation, Carbon, AudioToolbox | Part of macOS | Overlay, window list, hotkeys and sound on macOS |
| xdg-desktop-portal | LGPL-2.1-or-later | Global shortcuts on Wayland, over D-Bus |
| Flatpak runtime `org.freedesktop.Platform` 24.08 | Various, see the runtime | Only when running the Flatpak build |

### Build tools, not redistributed as libraries

| Tool | License | Purpose |
|---|---|---|
| .NET SDK | MIT | Compiling and publishing |
| Inno Setup 6 | Inno Setup License | Producing the Windows installer (its setup engine is embedded in the installer executable) |
| dpkg-deb | GPL-2.0-or-later | Producing the Ubuntu package |

---

## License texts

### MIT License

Applies to Avalonia UI, SkiaSharp, HarfBuzzSharp, MicroCom.Runtime, Tmds.DBus.Protocol and the .NET
Runtime, with the following copyright holders:

- Avalonia UI: Copyright © 2013-2025 The AvaloniaUI Project
- SkiaSharp and HarfBuzzSharp: Copyright © Microsoft Corporation
- MicroCom.Runtime: Copyright © 2021 Nikita Tsukanov
- Tmds.DBus.Protocol: Copyright © Tom Deseyn
- .NET Runtime: Copyright © .NET Foundation and Contributors

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The .NET Runtime itself incorporates further third-party components; see its
[THIRD-PARTY-NOTICES.TXT](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT).

### BSD 3-Clause License

Applies to ANGLE (Copyright 2018 The ANGLE Project Authors) and Skia (Copyright © 2011 Google Inc.).

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

  * Redistributions of source code must retain the above copyright notice,
    this list of conditions and the following disclaimer.

  * Redistributions in binary form must reproduce the above copyright notice,
    this list of conditions and the following disclaimer in the documentation
    and/or other materials provided with the distribution.

  * Neither the name of the copyright holder nor the names of its
    contributors may be used to endorse or promote products derived from
    this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

ANGLE's original license names "TransGaming Inc., Google Inc., 3DLabs Inc. Ltd." in the third
clause; the conditions are otherwise identical.

### HarfBuzz "Old MIT" License

Copyright © the HarfBuzz authors (see <https://github.com/harfbuzz/harfbuzz/blob/main/COPYING>).

```
Permission is hereby granted, without written agreement and without
license or royalty fees, to use, copy, modify, and distribute this
software and its documentation for any purpose, provided that the
above copyright notice and the following two paragraphs appear in
all copies of this software.

IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE TO ANY PARTY FOR
DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES
ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS DOCUMENTATION, EVEN
IF THE COPYRIGHT HOLDER HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH
DAMAGE.

THE COPYRIGHT HOLDER SPECIFICALLY DISCLAIMS ANY WARRANTIES, INCLUDING,
BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE. THE SOFTWARE PROVIDED HEREUNDER IS
ON AN "AS IS" BASIS, AND THERE IS NO OBLIGATION TO PROVIDE MAINTENANCE,
SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.
```

---

Trademarks: Windows is a trademark of Microsoft Corporation. Ubuntu is a trademark of Canonical Ltd.
Claude and Claude Code are trademarks of Anthropic, PBC. Desk Arcade is not affiliated with or
endorsed by any of these companies.
