# BigDvToolBox 绿色版

开发者：YanWei
项目：https://github.com/yanwei175/BigDvToolBox
下载：https://github.com/yanwei175/BigDvToolBox/releases

将整个 ZIP 解压到有读写权限的文件夹，运行 BigDvToolBox.exe。
请保留所有随附文件；不需要安装本软件，不需要另行安装 .NET 或 Windows App SDK。
支持 Windows 10 1809（17763）及以上的 x64 系统。
OCR 的原生组件需要 Microsoft Visual C++ 2015–2022 x64 运行库。
如果 OCR 提示缺少 MSVCP140/VCRUNTIME140，请从微软官方页面安装运行库：
https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist

升级：关闭程序，将新版本解压到新目录后运行。配置仍保存在当前用户的本地应用数据目录。
删除程序目录即可移除程序，用户配置需另行删除。这里的“绿色版”指无需安装，
并不表示所有用户数据都保存在程序目录。

帮助菜单中的 GitHub Releases 用于手动下载新版本，软件没有自动更新功能。

正常使用不生成打印诊断日志，也不启动后台打印事件收集。
旧版本已生成的日志可自行删除：`%LOCALAPPDATA%\BigDvToolBox\Logs\Printing`。
请正常双击启动程序，无需“以管理员身份运行”。程序与文件拖放来源的权限级别不一致可能影响拖放。

直接打印按实际大小（100%）放置印张，超出驱动可打印区域的内容裁切，不缩小整张拼版。
打印预览用灰色遮罩和虚线显示驱动报告的不可打印范围，这些辅助标记不会打印。
部分驱动在预览阶段尚未提供硬件边界，窗口会明确说明，最终按提交阶段返回的范围裁切。
若明确检测到自动缩放或每张纸多页，会停止提交并提示改为实际大小、每张纸一页。
票据读取失败或驱动未提供标准缩放字段时无法核实驱动缩放，应用侧仍按原尺寸绘制和裁切。

本项目按 AGPL-3.0-or-later 开源，第三方组件保留各自许可。
LICENSE、THIRD-PARTY-NOTICES.md、Licenses 文件夹包含相关声明。
对应项目源码 ZIP 与 Ghostscript 10.07.1 完整源码位于同一个 GitHub Release。
软件按现状提供，不附带任何担保。
