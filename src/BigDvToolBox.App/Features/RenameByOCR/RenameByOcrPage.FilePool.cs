using BigDvToolBox_App.Features.FilePool;
using Microsoft.UI.Xaml.Navigation;

namespace BigDvToolBox_App.Features.RenameByOCR;

public sealed partial class RenameByOcrPage : IFilePoolTarget
{
    FilePoolTarget? IFilePoolTarget.PoolTarget => _poolTarget;
    private FilePoolTarget? _poolTarget;
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not FilePoolNavigationContext context) return;
        ViewModel.ConnectFilePool(context.Service);
        ViewModel.PropertyChanged += PoolPreviewChanged;
        _poolTarget = new("OCR 重命名", ViewModel,
            () => ViewModel.CanConfigure, RenameByOcrViewModel.AcceptsPoolFile, async paths =>
            {
                var result = await ViewModel.ImportPoolFilesAsync(paths);
                await RefreshPreviewImageAsync();
                return result;
            }, ViewModel.ReportPoolMessage, () => ViewModel.IsBusy, () => { ViewModel.PropertyChanged -= PoolPreviewChanged; ViewModel.Dispose(); });
    }
    private async void PoolPreviewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RenameByOcrViewModel.CurrentPreviewBytes)) return;
        try { await RefreshPreviewImageAsync(); }
        catch (Exception ex) { ViewModel.ReportPoolMessage($"预览刷新失败：{ex.Message}"); }
    }
}
