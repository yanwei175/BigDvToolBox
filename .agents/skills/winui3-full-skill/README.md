# Microsoft WinUI Skills

本目录收录微软官方 [`microsoft/win-dev-skills`](https://github.com/microsoft/win-dev-skills) 仓库中的 WinUI Skill 套件，下载于 2026-07-22，对应插件版本 `0.3.0`。

| Skill | 用途 |
|---|---|
| `winui-design` | WinUI 3 界面设计、XAML、主题与无障碍 |
| `winui-dev-workflow` | 创建、构建、运行和诊断 WinUI 3 项目 |
| `winui-code-review` | MVVM、绑定、性能、安全和质量审查 |
| `winui-ui-testing` | 使用 Windows UI Automation 自动测试桌面应用 |
| `winui-packaging` | MSIX 打包、签名、CI/CD 与发布 |
| `winui-wpf-migration` | 将 WPF 应用迁移到 WinUI 3 |
| `winui-setup` | 安装并验证 WinUI 开发环境依赖 |
| `winui-session-report` | 生成智能体会话诊断报告 |

官方仓库当前仍处于 `v0.x` 预览阶段。`winui-design` 和 `winui-dev-workflow` 内含微软发布但尚未代码签名的可执行文件或 DLL；运行前应按官方仓库说明进行审查。

为兼容本仓库使用的 Codex Skill 校验规则，仅从 `winui-setup` 和 `winui-session-report` 的 frontmatter 中移除了 Codex 不支持的 `disable-model-invocation` 字段；其余官方 Skill 文件保持原样。

许可证见 [LICENSE](LICENSE)，第三方组件声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。原先从社区仓库复制并含项目专属内容的版本已保留在 `skills/_review/community-winui3-full-skill`。
