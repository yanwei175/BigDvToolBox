# 第三方组件

BigDvToolBox 原创代码：Copyright © 2026 YanWei，AGPL-3.0-or-later。
第三方组件保留原有版权与许可，不因本项目许可而改变。软件不附带担保。

| 组件 | 版本 | 许可/上游 |
| --- | --- | --- |
| GPL Ghostscript | 10.07.1 | AGPL-3.0-or-later；https://ghostscript.com/ |
| PDFsharp | 6.2.4 | MIT；https://github.com/empira/PDFsharp |
| Tesseract .NET wrapper | 5.2.0 | Apache-2.0；https://github.com/charlesw/tesseract |
| InteropDotNet（随 Tesseract wrapper） | 内嵌 | MIT；Copyright 2014 Andrey Akinshin；https://github.com/AndreyAkinshin/InteropDotNet |
| Tesseract OCR | 5.x（wrapper 内附原生 DLL） | Apache-2.0；https://github.com/tesseract-ocr/tesseract |
| Leptonica | 1.82.0 | BSD-2-Clause；http://www.leptonica.org/ |
| Tesseract tessdata（ces、eng、pol、rus） | 仓库内附数据 | Apache-2.0；https://github.com/tesseract-ocr/tessdata |
| CommunityToolkit.Mvvm | 8.4.2 | MIT；https://github.com/CommunityToolkit/dotnet |
| .NET / Microsoft.Extensions 等运行组件 | 10.0 / 8.0 | MIT 及各组件附带声明；https://github.com/dotnet |
| Windows App SDK | 2.5.1 | 随包 Microsoft 许可及第三方声明；https://github.com/microsoft/WindowsAppSDK |

许可原文和上游声明在程序目录的 Licenses 中；.NET 的原始许可和第三方声明在 Licenses/dotnet 中。微软的专有运行组件不属于本项目 AGPL 授权。

Ghostscript 的上游 LICENSE、COPYING、对应源码地址及固定 SHA-256 见
Licenses/ghostscript。本项目使用未修改的 gsdll32.dll / gsdll64.dll，
发行版按架构选择嵌入 DLL。字体/CMap 等上游许可例外以原始 LICENSE 为准。

对应源码由同一个 GitHub Release 提供：
https://github.com/yanwei175/BigDvToolBox/releases
请一并取得 BigDvToolBox-<版本>-source.zip 与 ghostscript-10.07.1.tar.gz。