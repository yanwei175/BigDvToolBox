using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace BigDvToolBox_App.Features.FilePool;

public sealed partial class FilePoolPanel : UserControl
{
    internal FilePoolViewModel ViewModel { get; } = new();
    internal event EventHandler? CloseRequested;
    public FilePoolPanel() => InitializeComponent();
    internal void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.CancelImport();
    private void Remove_Click(object sender, RoutedEventArgs e) => ViewModel.RemoveSelected();
    private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.Select(FilesList.SelectedItems.OfType<FilePoolEntry>().Select(item => item.Id));
    private async void Send_Click(object sender, RoutedEventArgs e)
    { if (await ViewModel.SendAsync()) CloseRequested?.Invoke(this, EventArgs.Empty); }
    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            picker.FileTypeFilter.Add("*");
            var files = await picker.PickMultipleFilesAsync();
            await ViewModel.ImportAsync(files.Select(f => f.Path));
        }
        catch (Exception ex) { ViewModel.Message = $"无法选择文件：{ex.Message}"; }
    }
    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) await ViewModel.ImportAsync([folder.Path]);
        }
        catch (Exception ex) { ViewModel.Message = $"无法选择文件夹：{ex.Message}"; }
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "清空文件池？",
            Content = "只移出文件池中的记录，不删除磁盘文件，也不移除各模块已有任务。",
            PrimaryButtonText = "清空", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) ViewModel.Clear();
    }
    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = ViewModel.CanEdit && e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.Handled = true;
    }
    private async void Panel_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!ViewModel.CanEdit || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            await ViewModel.ImportAsync(items.Select(i => i.Path));
        }
        catch (Exception ex) { ViewModel.Message = $"无法读取拖入的文件：{ex.Message}"; }
        finally { deferral.Complete(); }
    }
}
