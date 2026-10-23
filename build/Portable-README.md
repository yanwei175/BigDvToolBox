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

PDF 拼版打印诊断：每次点击打印都会在
`%LOCALAPPDATA%\BigDvToolBox\Logs\Printing` 中生成一份 `print-*.log`。
页面状态会显示本次日志完整路径，可以选中复制。
日志包含请求/实际纸张尺寸、DPI、位图像素和字节数、各阶段耗时，以及异常/HRESULT。
打印队列中的任务名称带短编号，用于匹配本次 Windows 打印错误事件。
遇到队列发送失败时，请保持软件打开约一分钟，再提供本次日志；
若系统不允许读取打印事件，日志会记录读取失败，其他打印诊断仍可使用。

本项目按 AGPL-3.0-or-later 开源，第三方组件保留各自许可。
LICENSE、THIRD-PARTY-NOTICES.md、Licenses 文件夹包含相关声明。
对应项目源码 ZIP 与 Ghostscript 10.07.1 完整源码位于同一个 GitHub Release。
软件按现状提供，不附带任何担保。
