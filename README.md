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

## OCR 文件重命名

左侧展开“OCR 设置”可选择识别语言、模型目录、重命名识别布局，以及是否关闭词典纠正。
默认语言为英语、捷克语、波兰语和俄语；布局默认跟随任务：固定区域使用单词模式，
锚点识别使用自动布局。CSV 导出始终使用整页自动布局，仍应用所选语言和词典选项。
设置自动保存到 `%LOCALAPPDATA%/BigDvToolBox/ocr-settings.json`，下次识别生效，
不会清除已经手动微调的目标文件名。

首次打开 OCR 工具会将四个内置模型预置到 `%LOCALAPPDATA%/BigDvToolBox/OCR/tessdata`。
仅首次预置，不覆盖同名文件；普通刷新不会重新补回用户删除的模型。
点击“打开目录”，添加官方 `.traineddata` 文件，再点击“刷新语言”并勾选即可使用新语言。
也可选择自己的模型目录；该目录需包含全部所选语言，不会混用或回退到内置目录。
缺失、为空或无法读取的所选模型会阻止 OCR，补齐文件或取消勾选后重试。
模型损坏或不支持 LSTM 时会在任务初始化阶段报告错误。

官方下载：[tessdata_best](https://github.com/tesseract-ocr/tessdata_best) 准确率优先，
[tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) 速度优先。
例如简体中文下载 `chi_sim.traineddata`，德语下载 `deu.traineddata`；保留原文件名，
将文件放在所选目录顶层。`osd` 和 `equ` 等辅助数据不会作为识别语言列出。
同一语言的 best 和 fast 使用相同文件名，使用不同目录分别保存并切换。
程序使用 Tesseract NuGet 5.2.0 的 LSTM 引擎，语言模型由用户手动下载，无自动下载或更新。
模型来源、文件哈希和许可见 [tessdata 声明](third_party/tessdata/README.md)。

## 下载

从 [GitHub Releases](https://github.com/yanwei175/BigDvToolBox/releases) 下载
BigDvToolBox-<版本>-win-x64.zip，解压后运行 BigDvToolBox.exe，保留目录中的全部文件。

支持 Windows 10 1809 及更高版本的 x64 系统，已包含 .NET 和 Windows App SDK。
OCR 需要 [Microsoft Visual C++ x64 运行库](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)。
用户配置保存在本地应用数据目录。新版通过 Releases 手动下载，软件没有自动更新。

当前开发版本为使用原生 TableView，采用 Windows App SDK **2.5.4-experimental**。
结果表格由控件处理固定表头和可调列宽，保持 48 DIP 行高和单行文本省略。
TableView 当前仅支持单行高亮选中；批量参与执行仍由每行复选框控制。

## 公共文件池

标题栏的“文件池 · 数量”打开右侧临时抽屉，覆盖页面而不压缩功能区域；点击外部、关闭按钮或 Esc 收起。
支持拖入文件、添加文件夹中的文件、搜索、类型筛选和多选，再主动加入当前功能。
默认只扫描直属文件，可选包含子文件夹；文件夹本身不入池。文件池只在本次运行期间共享，不恢复上次记录。

OCR 重命名、批量重命名的文件列表、PDF 字体转曲和图片处理的原有导入入口会自动收录输入文件。
OCR 支持通过文件池追加跨文件夹的 PDF、PNG、JPG、JPEG；原有“打开文件夹”仍替换当前 OCR 文件集合。
每个模块保留自己的任务、勾选和参数；移出或清空文件池不删除磁盘文件，也不移除已有任务。
重命名及文件夹改名会同步池中和其他模块的输入路径，并使相关预览失效。
PDF 转曲成功后用输出 PDF 替换池中的原 PDF，不删除磁盘原件；失败或取消的项保留原文件。
图片处理只保留池中的原输入，输出图片不入池。同一文件的冲突处理会提示稍后重试。

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
