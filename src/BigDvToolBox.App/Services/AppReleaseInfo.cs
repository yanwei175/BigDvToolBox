using System.Reflection;

namespace BigDvToolBox_App.Services;

/// <summary>界面和绿色版的版本、署名来自统一发布配置。</summary>
public static class AppReleaseInfo
{
    private static readonly Assembly AppAssembly = typeof(AppReleaseInfo).Assembly;
    public static string Version => AppAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? AppAssembly.GetName().Version?.ToString(3) ?? "未知";
    public static string Developer => AppAssembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "YanWei";
    public static string Copyright => AppAssembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
}
