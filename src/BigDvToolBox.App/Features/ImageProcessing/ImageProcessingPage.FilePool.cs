using BigDvToolBox_App.Features.FilePool;
using Microsoft.UI.Xaml.Navigation;

namespace BigDvToolBox_App.Features.ImageProcessing;

public sealed partial class ImageProcessingPage : IFilePoolTarget
{
    FilePoolTarget? IFilePoolTarget.PoolTarget => _poolTarget;
    private FilePoolTarget? _poolTarget;
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not FilePoolNavigationContext context) return;
        ViewModel.ConnectFilePool(context.Service);
        _poolTarget = new("图片处理", ViewModel,
            () => ViewModel.CanEditFiles, ViewModel.AcceptsPoolFile, async paths =>
            {
                var result = await ViewModel.ImportPoolFilesAsync(paths);
                
                return result;
            }, ViewModel.ReportPoolMessage, () => ViewModel.IsProcessing, () => { ViewModel.ConnectFilePool(null); });
    }
}
