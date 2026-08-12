# Third-party notices

Barline is distributed under [GPL-3.0-or-later](LICENSE). It bundles the components
below, which remain under their own licenses. Those licenses are reproduced here
because the components ship inside the released executable rather than beside it,
and their terms require the notice to travel with the copy.

---

## NAudio 2.2.1

Used for WASAPI loopback capture, which is what drives the visualizer.

Copyright © Mark Heath 2023
Authors: Mark Heath & Contributors
<https://github.com/naudio/NAudio>

Covers `NAudio`, `NAudio.Core`, `NAudio.Wasapi`, `NAudio.WinMM`, `NAudio.Asio`,
`NAudio.Midi` and `NAudio.WinForms`, all 2.2.1 and all MIT.

### The MIT License

Copyright 2020 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify,
merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be included in all copies
or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE
OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

---

## .NET and Windows Presentation Foundation

Releases are published **self-contained**: the .NET runtime and WPF are redistributed
inside the release rather than installed separately, so that Barline runs without the
user first installing a runtime. That makes their notices part of what ships.

Copyright © .NET Foundation and Contributors
Licensed under [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)
<https://github.com/dotnet/runtime> · <https://github.com/dotnet/wpf>

The redistributed files include the WPF native libraries seen beside the executable
(`PresentationNative_cor3.dll`, `wpfgfx_cor3.dll`, `PenImc_cor3.dll`,
`D3DCompiler_47_cor3.dll`) and the Visual C++ runtime `vcruntime140_cor3.dll`,
which the .NET Foundation redistributes as part of a self-contained deployment.

The .NET runtime itself incorporates further third-party components, each under its
own terms. Those are enumerated in the runtime's own notices, which apply in full to
the copy bundled here:
<https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>

If a release is ever published framework-dependent instead, the runtime is no longer
redistributed and this section does not apply to it.

---

## Lyrics data

Timed lyrics are fetched at runtime from [LRCLIB](https://lrclib.net), which places
its database in the public domain (CC0). No lyrics are bundled with the application;
they are downloaded only when the lyrics feature is switched on, and cached locally.
Lyrics remain the work of their respective authors and publishers.
