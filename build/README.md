# 绿色版发布

## 在 Visual Studio 中

选择 BigDvToolBox.App → 发布 → Portable-x64 → 发布。
如果当前还选着旧的 NSIS-x64 配置，请切换到 Portable-x64。
不再使用 NSIS，也不生成安装 EXE 或 MSI。

该配置会在编译前确定新版本，生成自包含的 x64 应用，然后打包为 ZIP。
只有全部附件生成成功才更新 build/Release.props 的补丁号；普通编译不递增。
开发者署名保持 YanWei。不使用网络共享目录，不写入在线更新索引。

## 命令行

在仓库根目录执行：

    powershell -NoProfile -ExecutionPolicy Bypass -File .\build\Publish-Release.ps1

需要 .NET 10 SDK、VS 2026 的 WinUI/.NET 桌面构建组件和 Windows SDK。
首次打包会从官方地址下载 Ghostscript 10.07.1 源码，并校验固定 SHA-256；
也可事先把 source.json 指定的文件放到 artifacts/third-party。

## GitHub Releases

输出位于 artifacts/releases/<版本>/，包含：

- BigDvToolBox-<版本>-win-x64.zip：用户下载的绿色版。
- BigDvToolBox-<版本>-source.zip：与该二进制版本相匹配的工作树源码。
- ghostscript-10.07.1.tar.gz：对应 Ghostscript 完整源码。
- ReleaseNotes.md 和 SHA256SUMS。

发布前检查 build/ReleaseNotes.md。打包完成后提交本次源代码和更新后的
build/Release.props，推送到 GitHub。在仓库 Releases 中创建 v<版本>，
选择对应提交，将同一版本目录的以上全部文件一起上传，再发布。

GitHub 自动生成的源码压缩包不包含 Ghostscript 上游源码，不能替代这套附件。
重新分发时保留完整授权文件和对应源码；不同版本不要覆盖同名文件。

应用 ZIP 不含安装器。解压后直接运行 BigDvToolBox.exe；OCR 依赖 VC++ x64 运行库，
详见 ZIP 内 README.md。用户配置保存在本地应用数据目录，视图缩放和平移不保存。