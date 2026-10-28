using BigDvToolBox_App.Features.FilePool;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace BigDvToolBox_App;

public sealed partial class MainWindow
{
    private FilePoolService _filePool = null!;
    private void InitializeFilePool()
    {
        _filePool = new(action =>
        {
            if (DispatcherQueue.HasThreadAccess) { action(); return Task.CompletedTask; }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                try { action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
            })) completion.SetException(new InvalidOperationException("窗口已关闭。"));
            return completion.Task;
        });
        PoolPanel.ViewModel.Connect(_filePool);
        _filePool.Changed += (_, _) => FilePoolButton.Content = $"文件池 · {_filePool.Entries.Count}";
        PoolPanel.CloseRequested += (_, _) => FilePoolDrawer.IsPaneOpen = false;
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(FilePool_KeyDown), true);
        Closed += (_, _) =>
        {
            PoolPanel.ViewModel.Dispose();
            foreach (var tab in _openTabs.Values)
                if (tab.Content is Frame { Content: IFilePoolTarget participant }) participant.PoolTarget?.Dispose();
        };
    }
    private void UpdateFilePoolTarget()
    {
        var page = (ToolTabView.SelectedItem as TabViewItem)?.Content as Frame;
        PoolPanel.ViewModel.SetTarget((page?.Content as IFilePoolTarget)?.PoolTarget);
    }
    private async void FilePoolButton_Click(object sender, RoutedEventArgs e)
    {
        FilePoolDrawer.IsPaneOpen = !FilePoolDrawer.IsPaneOpen;
        if (FilePoolDrawer.IsPaneOpen)
        {
            UpdateFilePoolTarget();
            try { await PoolPanel.ViewModel.RefreshAsync(); }
            catch (Exception ex) { PoolPanel.ViewModel.Message = $"文件状态检查失败：{ex.Message}"; }
        }
    }
    private void FilePoolDrawer_PaneOpened(SplitView sender, object args) => PoolPanel.FocusSearch();
    private void FilePoolDrawer_PaneClosed(SplitView sender, object args) => FilePoolButton.Focus(FocusState.Programmatic);
    private void FilePoolDrawer_SizeChanged(object sender, SizeChangedEventArgs e) =>
        FilePoolDrawer.OpenPaneLength = Math.Min(360, Math.Max(0, e.NewSize.Width));
    private void FilePool_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || !FilePoolDrawer.IsPaneOpen) return;
        FilePoolDrawer.IsPaneOpen = false; e.Handled = true;
    }
}
