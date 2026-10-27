using CommunityToolkit.Mvvm.ComponentModel;

namespace BigDvToolBox_App.Features.BatchRename;

public enum RenameTableColumn { Original, Target, Directory, Status }

/// <summary>表头与所有行共用列宽；文件与文件夹各持有一份。</summary>
public partial class RenameTableColumns : ObservableObject
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TotalWidth))]
    public partial double OriginalWidth { get; set; } = 160;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TotalWidth))]
    public partial double TargetWidth { get; set; } = 180;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TotalWidth))]
    public partial double DirectoryWidth { get; set; } = 220;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TotalWidth))]
    public partial double StatusWidth { get; set; } = 240;

    // 勾选列 40 + 左右内边距 24 + 四段列间距 48。
    public double TotalWidth => 112 + OriginalWidth + TargetWidth + DirectoryWidth + StatusWidth;

    public void Resize(RenameTableColumn column, double delta)
    {
        if (!double.IsFinite(delta)) return;
        double Clamp(double width) => Math.Clamp(width + delta, 80, 1200);
        switch (column)
        {
            case RenameTableColumn.Original: OriginalWidth = Clamp(OriginalWidth); break;
            case RenameTableColumn.Target: TargetWidth = Clamp(TargetWidth); break;
            case RenameTableColumn.Directory: DirectoryWidth = Clamp(DirectoryWidth); break;
            case RenameTableColumn.Status: StatusWidth = Clamp(StatusWidth); break;
        }
    }
}
