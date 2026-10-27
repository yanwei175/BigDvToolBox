# BigDvToolBox

Windows 桌面工具箱，使用 C# / .NET 10 / WinUI 3（Windows App SDK 2.5）。

包含 PDF 拼版与矢量导出、系统打印、PDF 字体转曲、OCR 文件重命名、
图片批处理、单位转换等工具。

## 下载

从 [GitHub Releases](https://github.com/yanwei175/BigDvToolBox/releases) 下载
BigDvToolBox-<版本>-win-x64.zip，解压后运行 BigDvToolBox.exe，保留目录中的全部文件。

支持 Windows 10 1809 及更高版本的 x64 系统，已包含 .NET 和 Windows App SDK。
OCR 需要 [Microsoft Visual C++ x64 运行库](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)。
用户配置保存在本地应用数据目录。新版通过 Releases 手动下载，软件没有自动更新。

## 开发与发布

需要 .NET 10 SDK、Visual Studio 2026 的 Windows/WinUI 开发组件和 Windows SDK。
代码位于 src/BigDvToolBox.App，主窗口的 ToolPages 注册表维护工具导航。

    dotnet restore BigDvToolBox.slnx
    .\.agents\skills\winui3-full-skill\winui-dev-workflow\BuildAndRun.ps1 -Project src\BigDvToolBox.App\BigDvToolBox.App.csproj -UseMSBuild

在 VS 中选择 Portable-x64 发布配置即可生成绿色版 ZIP 并递增补丁版本号。
命令行和 GitHub Release 附件说明见 [build/README.md](build/README.md)。

## 许可与反馈

Copyright © 2026 YanWei。本项目原创代码按
[GNU AGPL v3 或更高版本](LICENSE)发布，不附带任何担保。
第三方代码、库和数据保留原作者版权与各自许可，见
[第三方声明](src/BigDvToolBox.App/THIRD-PARTY-NOTICES.md)。

PDF 字体转曲使用未修改的 Ghostscript 10.07.1（AGPL-3.0-or-later）；
仓库包含 x86/x64 DLL，授权说明见 [third_party/ghostscript](third_party/ghostscript)。
发行版一并提供本项目对应源码和该版本 Ghostscript 完整源码。

问题和建议请提交到 [Issues](https://github.com/yanwei175/BigDvToolBox/issues)。