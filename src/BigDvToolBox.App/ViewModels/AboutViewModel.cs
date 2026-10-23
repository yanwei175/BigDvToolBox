using BigDvToolBox_App.Services;

namespace BigDvToolBox_App.ViewModels;

/// <summary>标题栏关于信息，版本与署名来自统一发布配置。</summary>
public sealed class AboutViewModel
{
    public string AppVersion => AppReleaseInfo.Version;
    public string Developer => AppReleaseInfo.Developer;
    public string Copyright => AppReleaseInfo.Copyright;
    public Uri RepositoryUri { get; } = new("https://github.com/yanwei175/BigDvToolBox");
    public Uri ReleasesUri { get; } = new("https://github.com/yanwei175/BigDvToolBox/releases");
    public Uri IssuesUri { get; } = new("https://github.com/yanwei175/BigDvToolBox/issues");
    public Uri LicenseUri { get; } = new("https://github.com/yanwei175/BigDvToolBox/blob/master/LICENSE");
}
