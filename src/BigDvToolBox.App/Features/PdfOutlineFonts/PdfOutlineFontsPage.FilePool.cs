using BigDvToolBox_App.Features.FilePool;
using Microsoft.UI.Xaml.Navigation;

namespace BigDvToolBox_App.Features.PdfOutlineFonts;

public sealed partial class PdfOutlineFontsPage : IFilePoolTarget
{
    FilePoolTarget? IFilePoolTarget.PoolTarget => _poolTarget;
    private FilePoolTarget? _poolTarget;
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not FilePoolNavigationContext context) return;
        ViewModel.ConnectFilePool(context.Service);
        _poolTarget = new("PDF 字体转曲", ViewModel,
            () => ViewModel.CanConfigure, ViewModel.AcceptsPoolFile, async paths =>
            {
                var result = await ViewModel.ImportPoolFilesAsync(paths);
                
                return result;
            }, ViewModel.ReportPoolMessage, () => ViewModel.IsConverting, () => { ViewModel.ConnectFilePool(null); });
    }
}
