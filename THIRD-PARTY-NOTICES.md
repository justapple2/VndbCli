# 第三方许可与数据来源

本项目原创代码采用[Apache-2.0](LICENSE)。第三方组件保留原许可证，不因本项目许可而重新授权。锁定版本、用途、作者与逐项声明见[依赖清单](licenses/dependencies.json)。

| 组件 | 适用许可／声明 |
| --- | --- |
| Avalonia及平台适配 | [MIT](licenses/Avalonia-MIT.txt)；ANGLE原生组件另保留BSD声明 |
| CommunityToolkit.Mvvm | MIT及随包第三方声明，见依赖清单 |
| .NET、ASP.NET Core、Microsoft.Data.Sqlite、System.CommandLine | MIT及随包第三方声明 |
| SQLitePCL.raw | [Apache-2.0](LICENSE)，Copyright 2014-2024 SourceGear, LLC |
| SkiaSharp／HarfBuzzSharp及原生组件 | MIT与完整复合第三方声明，见依赖清单；不以封装包许可替代内部组件条款 |
| MicroCom、Tmds.DBus、ZstdSharp.Port | MIT，原版权与许可全文随附 |
| xUnit测试组件 | Apache-2.0及其原MIT代码声明 |
| Inter字体（测试／演示） | [SIL Open Font License 1.1](licenses/Inter-OFL-1.1.txt)，字体本身不改为Apache许可 |
| Fluent UI System Icons路径子集 | 下述Microsoft MIT许可 |

`licenses/`保留依赖随包的许可与完整第三方通知，重复文本合并存放；逐包引用仍保留。原生通知可能列出可选／多重许可条款，应依各组件实际使用方式遵循，不将这些文本全部当作本项目代码的许可证。

VNDB公共数据受其[Data License](https://vndb.org/d17#4)及官方下载包的ODbL／DbCL说明约束；API使用另遵循[官方使用条款](https://api.vndb.org/kana#usage-terms)。本项目的代码许可不授予数据库内容、游戏或图片的再分发权。分发公共数据包时保留其原许可、来源、时间戳和摘要；个人库及密钥应私下保管。

README截图来自历史合成演示，使用自制占位封面，不含真实收藏数据或第三方游戏封面。

## Fluent UI System Icons

The embedded vector path subset in `src/Vndb.Desktop/Resources/Icons.axaml` comes from Microsoft Fluent UI System Icons via the Avalonia Fluent icons catalog:

- https://github.com/microsoft/fluentui-system-icons
- https://avaloniaui.github.io/icons.html

MIT License

Copyright (c) 2020 Microsoft Corporation

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
