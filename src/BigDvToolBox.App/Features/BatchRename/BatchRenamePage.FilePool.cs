using BigDvToolBox_App.Features.FilePool;
using Microsoft.UI.Xaml.Navigation;

namespace BigDvToolBox_App.Features.BatchRename;

public sealed partial class BatchRenamePage : IFilePoolTarget
{
    FilePoolTarget? IFilePoolTarget.PoolTarget => _poolTarget;
    private FilePoolTarget? _poolTarget;
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not FilePoolNavigationContext context) return;
        ViewModel.ConnectFilePool(context.Service);
        _poolTarget = new("批量重命名", ViewModel,
            () => ViewModel.CanEdit, ViewModel.AcceptsPoolFile, async paths =>
            {
                var result = await ViewModel.ImportPoolFilesAsync(paths);
                
                return result;
            }, ViewModel.ReportPoolMessage, () => ViewModel.IsBusy, () => { ViewModel.ConnectFilePool(null); });
    }
}
