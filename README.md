# BigDvToolBox

Windows 桌面工具箱，使用 C# / .NET 10 / WinUI 3（Windows App SDK 2.5）。

包含 PDF 拼版与矢量导出、系统打印、PDF 字体转曲、OCR 文件重命名、
批量文件／文件夹重命名、图片批处理、单位转换等工具。

## 批量重命名

左侧按顺序组合替换文字、添加文字、按位置删除、删除指定字符、大小写转换、
添加序号六类方法；同类方法可以重复添加，每个方法独立配置和启用。
支持第 N 次替换、从右侧定位，以及逐方法选择处理文件名或完整名称。
文件夹名称中的点不视为扩展名，位置按完整 Unicode 字符计数。

右侧通过“重命名文件”和“重命名文件夹”标签管理不同清单，支持拖放、勾选、
排序和预览。开始按钮只处理当前标签页的勾选项。目标已存在、名称无效、
或同时包含父子目录时会阻止执行，不覆盖已有项目。名称交换使用临时名称；
取消或失败时尝试恢复原名称，恢复不完整时显示实际路径。此恢复机制不保证断电恢复。

方法列表可以保存为本地预设并导入／导出 JSON。本地预设位于
`%LOCALAPPDATA%/BigDvToolBox/RenamePresets`，不包含待处理文件路径。
纯逻辑位于 `src/BigDvToolBox.Renaming.Core`，不依赖 WinUI；核心测试可独立运行：

    dotnet test tests/BigDvToolBox.Renaming.Core.Tests

## 下载

从 [GitHub Releases](https://github.com/yanwei175/BigDvToolBox/releases) 下载
BigDvToolBox-<版本>-win-x64.zip，解压后运行 BigDvToolBox.exe，保留目录中的全部文件。

支持 Windows 10 1809 及更高版本的 x64 系统，已包含 .NET 和 Windows App SDK。
OCR 需要 [Microsoft Visual C++ x64 运行库](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)。
用户配置保存在本地应用数据目录。新版通过 Releases 手动下载，软件没有自动更新。

当前开发版本为使用原生 TableView，采用 Windows App SDK **2.5.4-experimental**。
结果表格由控件处理固定表头和可调列宽，保持 48 DIP 行高和单行文本省略。
TableView 当前仅支持单行高亮选中；批量参与执行仍由每行复选框控制。

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
