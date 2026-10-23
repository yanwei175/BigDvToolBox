using Microsoft.UI.Xaml.Controls;

namespace BigDvToolBox_App.Navigation;

/// <summary>
/// 描述一个可以从主导航打开的工具页面。
/// NavigationView 和 TabView 共用这份元数据，避免在两处重复维护标题和图标。
/// </summary>
internal sealed record ToolPageDefinition(
    string Id,
    string Title,
    Symbol Icon,
    Type PageType,
    string Description = "",
    bool IsClosable = true,
    bool ShowOnHome = true);

/// <summary>
/// 首页使用的导航上下文。首页与左侧导航共享页面注册数据和打开入口。
/// </summary>
internal sealed record HomePageNavigationContext(
    IReadOnlyList<ToolPageDefinition> Tools,
    Action<string> OpenTool);
