using BigDvToolBox.Renaming.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace BigDvToolBox_App.Features.BatchRename;

public sealed partial class BatchRenamePage : Page
{
    internal BatchRenameViewModel ViewModel { get; } = new();
    private bool _interacting;
    private bool _suppressPreset;
    private bool _initialized;
    private RenamePreset? _activePreset;
    private Guid[] _methodOrderBeforeDrag = [];
    private RenameMethodViewModel? _draggedMethod;
    private readonly string _methodDragToken = Guid.NewGuid().ToString("N");

    public BatchRenamePage()
    {
        InitializeComponent();
        // 编辑器中的输入控件可能处理指针事件，仍需同步当前方法的选中状态。
        MethodList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(MethodItem_PointerPressed), true);
        MethodList.GotFocus += MethodItem_GotFocus;
    }

    public static Visibility SelectionVisibility(bool selected) => selected ? Visibility.Visible : Visibility.Collapsed;
    public static bool HasPreset(object? preset) => preset is RenamePreset;

    private void MethodList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (!ViewModel.CanEdit) { e.Cancel = true; return; }
        _methodOrderBeforeDrag = ViewModel.Methods.Select(m => m.Id).ToArray();
        _draggedMethod = e.Items.OfType<RenameMethodViewModel>().FirstOrDefault();
        if (_draggedMethod is not null) ViewModel.SelectedMethod = _draggedMethod;
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.Properties["BigDvRenameMethod"] = _methodDragToken;
    }

    private void MethodList_DragOver(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanEdit || !e.DataView.Properties.TryGetValue("BigDvRenameMethod", out var token) ||
            !Equals(token, _methodDragToken))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            e.Handled = true;
        }
    }

    private void MethodList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        ViewModel.CompleteMethodReorder(_draggedMethod, _methodOrderBeforeDrag);
        _draggedMethod = null;
        _methodOrderBeforeDrag = [];
    }

    private void MethodItem_PointerPressed(object sender, PointerRoutedEventArgs e) => SelectMethodFromSource(e.OriginalSource);
    private void MethodItem_GotFocus(object sender, RoutedEventArgs e) => SelectMethodFromSource(e.OriginalSource);

    private void SelectMethodFromSource(object source)
    {
        if (!ViewModel.CanEdit) return;
        for (var element = source as DependencyObject; element is not null && element != MethodList; element = VisualTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { DataContext: RenameMethodViewModel method })
            {
                ViewModel.SelectedMethod = method;
                return;
            }
        }
    }

    private void RemoveMethod_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEdit || sender is not FrameworkElement { DataContext: RenameMethodViewModel method }) return;
        ViewModel.SelectedMethod = method;
        ViewModel.RemoveMethodCommand.Execute(null);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        await ViewModel.LoadPresetsAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    internal async Task<bool> CanCloseAsync()
    {
        if (_interacting) return false;
        if (ViewModel.IsBusy)
        {
            ViewModel.StatusText = "请先取消当前操作，等待执行和恢复结果后再关闭此页面。";
            return false;
        }
        _interacting = true;
        try { return await CanReplaceMethodsAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        { ViewModel.StatusText = $"保存失败：{ex.Message}"; return false; }
        finally { _interacting = false; }
    }

    private async Task InteractAsync(Func<Task> operation)
    {
        if (_interacting || !ViewModel.CanEdit) return;
        _interacting = true;
        try { await operation(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or System.Runtime.InteropServices.COMException)
        { ViewModel.StatusText = $"操作失败：{ex.Message}"; }
        finally { _interacting = false; }
    }

    private void AddMethod_Click(object sender, RoutedEventArgs e)
    {
        if (_interacting || !ViewModel.CanEdit || sender is not AppBarButton { Tag: string tag } ||
            !Enum.TryParse<RenameMethod>(tag, out var method)) return;
        ViewModel.AddMethod(method);
        RenameMethodFlyout.Hide();
        MethodList.ScrollIntoView(ViewModel.SelectedMethod);
    }

    private void Method_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        if (sender.DataContext is not RenameMethodViewModel selected) return;
        ViewModel.SelectedMethod = selected;
        foreach (var method in ViewModel.Methods) if (method != selected) method.IsExpanded = false;
    }

    private async void AddItems_Click(object sender, RoutedEventArgs e) => await InteractAsync(async () =>
    {
        if (ViewModel.IsFolderMode)
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            var result = await picker.PickSingleFolderAsync();
            if (result is not null) await ViewModel.AddPathsAsync([result.Path]);
        }
        else
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            picker.FileTypeFilter.Add("*");
            var results = await picker.PickMultipleFilesAsync();
            await ViewModel.AddPathsAsync(results.Select(r => r.Path));
        }
    });

    private async void AddContents_Click(object sender, RoutedEventArgs e) => await InteractAsync(async () =>
    {
        var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        var result = await picker.PickSingleFolderAsync();
        if (result is not null) await ViewModel.AddPathsAsync([result.Path], contents: true, recursive: ViewModel.IncludeSubdirectories);
    });

    private void RemoveRows_Click(object sender, RoutedEventArgs e)
    {
        var table = ViewModel.IsFolderMode ? FolderTable : FileTable;
        if (table.SelectedItem is RenameRowViewModel selected) ViewModel.RemoveRows([selected]);
    }

    private void ClearRows_Click(object sender, RoutedEventArgs e) => ViewModel.RemoveRows(ViewModel.CurrentRows.ToArray());
    private void CheckAll_Click(object sender, RoutedEventArgs e) => ViewModel.SetIncluded(true);
    private void UncheckAll_Click(object sender, RoutedEventArgs e) => ViewModel.SetIncluded(false);
    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    private void Items_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = ViewModel.CanEdit && e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.DragUIOverride.Caption = ViewModel.IsFolderMode ? "添加文件夹本身" : "添加文件，或添加文件夹中的文件";
    }

    private async void Items_Drop(object sender, DragEventArgs e) => await InteractAsync(async () =>
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        await ViewModel.AddPathsAsync(items.Select(item => item.Path), recursive: ViewModel.IncludeSubdirectories);
    });

    private void SetPresetSelection(RenamePreset? preset)
    {
        _suppressPreset = true;
        try { PresetCombo.SelectedItem = preset; _activePreset = preset; }
        finally { _suppressPreset = false; }
    }

    private async void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPreset || PresetCombo.SelectedItem is not RenamePreset requested) return;
        SetPresetSelection(_activePreset);
        await InteractAsync(async () =>
        {
            if (!await CanReplaceMethodsAsync()) return;
            // 保存旧预设可能替换集合对象，重新按名称定位请求的预设。
            var target = ViewModel.Presets.FirstOrDefault(p => p.Name == requested.Name) ?? requested;
            ViewModel.ApplyPreset(target); SetPresetSelection(target);
        });
    }

    private async Task<bool> CanReplaceMethodsAsync()
    {
        if (!ViewModel.HasUnsavedChanges) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "保存当前方案列表？", Content = "当前方法及参数有未保存的修改。",
            PrimaryButtonText = "保存并继续", SecondaryButtonText = "放弃修改", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Secondary || result == ContentDialogResult.Primary && await SaveCurrentPresetAsync();
    }

    private async void SavePreset_Click(object sender, RoutedEventArgs e) => await InteractAsync(async () => { await SaveCurrentPresetAsync(); });

    private async void DeletePreset_Click(object sender, RoutedEventArgs e) => await InteractAsync(async () =>
    {
        if (PresetCombo.SelectedItem is not RenamePreset preset) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "删除预设？",
            Content = $"删除本地预设“{preset.Name}”？当前方法和参数将保留，可重新保存。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _suppressPreset = true;
        try
        {
            await ViewModel.DeletePresetAsync(preset);
            SetPresetSelection(null);
        }
        finally { _suppressPreset = false; }
        ViewModel.StatusText = $"已删除预设“{preset.Name}”，当前方法和参数已保留。";
    });

    private async Task<bool> SaveCurrentPresetAsync()
    {
        var name = new TextBox { Header = "预设名称（同名将更新）", Text = _activePreset?.Name ?? "我的重命名方案", MaxLength = 100 };
        AutomationProperties.SetAutomationId(name, "RenamePresetName");
        var export = new CheckBox { Content = "同时导出为 JSON 文件" };
        AutomationProperties.SetAutomationId(export, "ExportRenamePreset");
        var panel = new StackPanel { Spacing = 12, MinWidth = 320 }; panel.Children.Add(name); panel.Children.Add(export);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "保存方案列表", Content = panel,
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        _suppressPreset = true;
        RenamePreset preset;
        try { preset = await ViewModel.SavePresetAsync(name.Text); }
        finally { _suppressPreset = false; }
        SetPresetSelection(preset);
        ViewModel.StatusText = $"已保存预设“{preset.Name}”。";
        if (export.IsChecked == true)
        {
            var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { SuggestedFileName = "重命名方案" };
            picker.FileTypeChoices.Add("JSON 方案列表", [".json"]);
            var result = await picker.PickSaveFileAsync();
            if (result is not null) await File.WriteAllTextAsync(result.Path, ViewModel.SerializePreset(preset.Name));
        }
        return true;
    }

    private async void LoadPreset_Click(object sender, RoutedEventArgs e) => await InteractAsync(async () =>
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        picker.FileTypeFilter.Add(".json");
        var result = await picker.PickSingleFileAsync();
        if (result is null) return;
        if (new FileInfo(result.Path).Length > 2_000_000) throw new ArgumentException("预设文件过大。");
        var preset = RenamePreset.Parse(await File.ReadAllTextAsync(result.Path));
        if (!await CanReplaceMethodsAsync()) return;
        _suppressPreset = true;
        try { ViewModel.UpsertPreset(preset); ViewModel.ApplyPreset(preset); }
        finally { _suppressPreset = false; }
        SetPresetSelection(preset);
        ViewModel.MarkPresetUnsaved();
        ViewModel.StatusText = $"已加载“{preset.Name}”，如需下次使用，请保存方案列表。";
    });
}
