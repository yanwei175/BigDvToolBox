# BigDvToolBox

一个用于学习 WinUI 3 的个人桌面工具箱。

## 技术栈

- .NET 10
- C# / XAML
- WinUI 3
- Windows App SDK 1.8
- 单项目 MSIX 打包

## 环境要求

- Windows 10 2004（Build 19041）或更高版本
- .NET SDK 10.0.302 或兼容的 10.0 Feature Band
- Visual Studio 2026（安装 Windows 应用开发相关组件），或使用 .NET CLI
- Windows 开发者模式

## 开始使用

```powershell
dotnet restore BigDvToolBox.slnx
dotnet build BigDvToolBox.slnx
dotnet run --project src/BigDvToolBox.App/BigDvToolBox.App.csproj -p:Platform=x64
```

应用代码位于 `src/BigDvToolBox.App`。仓库专用的 Codex Skill 放在
`.agents/skills/<skill-name>/SKILL.md`，项目级 Codex 配置放在
`.codex/config.toml`。

## 添加工具页面

主窗口采用 `NavigationView + TabView` 混合导航：左侧导航负责发现工具，
右侧标签页保留已打开的工具。每个工具默认只有一个标签实例。

创建新的 WinUI `Page` 后，在 `MainWindow.xaml.cs` 的 `ToolPages` 注册表中
加入一条 `ToolPageDefinition` 即可。NavigationView 和 TabView 会共用注册项的
ID、标题、图标和页面类型，无需分别维护两套导航代码。
