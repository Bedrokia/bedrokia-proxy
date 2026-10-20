# Third-party notices

Bedrokia Proxy (whose protocol and NetherNet code come from Bedrokia) is licensed under the Apache License 2.0 (see `LICENSE`). It contains code and data **ported or adapted**
from the projects below, and ships the listed libraries in its executable. Their licenses and copyright notices
are reproduced here, as those licenses require.

Code from PocketMine-MP, PowerNukkitX (LGPL) and Bedrock Dedicated Server (proprietary) is **never** copied.

## Ported code and data

| Project | Used for | License | Copyright |
|---|---|---|---|
| [Dragonfly](https://github.com/df-mc/dragonfly) | Game logic: blocks, items, inventory, enchanting, entities physics, vanilla data files in `src/Bedrokia.Server/Data` | MIT | Copyright (c) 2019 Dragonfly Tech |
| [gophertunnel](https://github.com/Sandertv/gophertunnel) | Bedrock protocol: packet layouts, login, NetherNet | MIT | Copyright (c) 2019 Sandertv |
| [Glowstone](https://github.com/GlowstoneMC/Glowstone) | World generation: biome layers, terrain density | MIT | Glowstone Copyright (C) 2015-2022 The Glowstone Project. Glowstone Copyright (C) 2011-2014 Tad Hardesty. Lightstone Copyright (C) 2010-2011 Graham Edgecombe. |
| [minecraft-data (PrismarineJS)](https://github.com/PrismarineJS/minecraft-data) | Biome data | MIT | See the LICENSE file of the repository |
| [Cuberite](https://github.com/cuberite/cuberite) | Generation details | Apache License 2.0 | The Cuberite Team |

## Libraries in the executable (NuGet)

| Library | License |
|---|---|
| [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) (WebRTC for NetherNet) | BSD 3-Clause, see the project |
| [Roslyn / Microsoft.CodeAnalysis.CSharp](https://github.com/dotnet/roslyn) (source plugins) | MIT, Copyright (c) .NET Foundation and Contributors |
| [YamlDotNet](https://github.com/aaubry/YamlDotNet) (plugin.yml, config.yml) | MIT, Copyright (c) Antoine Aubry and contributors |
| .NET runtime | MIT, Copyright (c) .NET Foundation and Contributors |

## MIT License text

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
