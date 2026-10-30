# Third-party notices

RynthSuite ships the following third-party material inside its own files.

## UtilityBelt (AutoTrade)

- What: RynthAi's AutoTrade is a port of UtilityBelt's AutoTrade tool (`UtilityBelt/Tools/AutoTrade.cs`, with the item-safety checks from `UtilityBelt/Lib/Util.cs`): its behaviour, settings, commands, profile folders and auto-accept lists, rewritten for RynthCore.
- Where: `Plugins/RynthCore.Plugin.RynthAi/Trade/AutoTradeManager.cs` and `Plugins/RynthCore.Plugin.RynthAi/Trade/AutoTradeCommands.cs`, compiled into `RynthCore.Plugin.RynthAi.dll`.
- Source: https://gitlab.com/utilitybelt/utilitybelt.gitlab.io (the UtilityBelt contributors). Ported from the public source only.
- Licence: MIT (the repository's README, "License: MIT"). The repository has no LICENSE file or copyright line, so the notice below names the UtilityBelt contributors.

```
MIT License

Copyright (c) the UtilityBelt contributors

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

## Chorizite DatReaderWriter (format reference for the dat reader)

- What: the layout of Asheron's Call .dat files (header, block chains, the directory B-tree), used as the reference when RynthSuite's own dat reader was written (2026-10-05). The code is RynthSuite's own (MIT); this notice is kept because the library was the format reference.
- Where: `Shared/RynthCore.TerrainData/DatDatabase.cs`, compiled into `RynthCore.TerrainData.dll`. The notice is also in that file's header.
- Source: https://github.com/Chorizite/DatReaderWriter (ACClientLib)
- Licence: MIT, reproduced below.

```
MIT License

Copyright 2024 ACClientLib

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
