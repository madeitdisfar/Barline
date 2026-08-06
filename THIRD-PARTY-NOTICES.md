# Third-party notices

Barline is distributed under [GPL-3.0-or-later](LICENSE). It bundles the components
below, which remain under their own licences. Those licences are reproduced here
because the components ship inside the released executable rather than beside it,
and their terms require the notice to travel with the copy.

---

## NAudio 2.2.1

Used for WASAPI loopback capture, which is what drives the visualiser.

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

Barline targets the .NET Desktop Runtime, which is not redistributed here — it is
installed separately, or supplied by the platform. Its licence is
[MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT), Copyright © .NET
Foundation and Contributors.

---

## Lyrics data

Timed lyrics are fetched at runtime from [LRCLIB](https://lrclib.net), which places
its database in the public domain (CC0). No lyrics are bundled with the application;
they are downloaded only when the lyrics feature is switched on, and cached locally.
Lyrics remain the work of their respective authors and publishers.
