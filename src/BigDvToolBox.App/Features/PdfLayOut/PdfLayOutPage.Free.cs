using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;

namespace BigDvToolBox_App.Features.PdfLayOut;

public sealed partial class PdfLayOutPage
{
    private const string CellDragFormat = "BigDvToolBox.PdfLayOut.Cell";
    private readonly string _dragSession = Guid.NewGuid().ToString("N");
    private PdfLayOutCellContent? _dragContent;
    private const double CellDragThreshold = 6;
    private (Button Source, uint PointerId, Windows.Foundation.Point Position)? _cellDragPress;
    private bool _isCellDragActive;
    private readonly Dictionary<PdfLayOutCellViewModel, Button> _freeVisuals = [];
    private readonly Dictionary<PdfLayOutPagePreview, Task<BitmapImage>> _freeBitmapTasks = [];

    private void BuildFreeCells(PdfLayOutSheetLayout layout)
    {
        _cellDragPress = null;
        _freeVisuals.Clear();
        var needed = ViewModel.FreeCells.Where(c => c.Preview is not null).Select(c => c.Preview!).ToHashSet();
        foreach (var key in _freeBitmapTasks.Keys.Except(needed).ToArray()) _freeBitmapTasks.Remove(key);
        var contents = ViewModel.FreeCells.ToDictionary(c => (c.Row, c.Column));
        var placements = layout.Placements.ToDictionary(p => (p.Row, p.Column));
        foreach (PdfLayOutCell bounds in layout.Cells)
        {
            if (!contents.TryGetValue((bounds.Row, bounds.Column), out var cell)) continue;
            var visual = (Button)((DataTemplate)Resources["FreeCellTemplate"]).LoadContent();
            visual.Tag = cell;
            visual.AllowDrop = ViewModel.IsFreeMode;
            visual.IsTabStop = true;
            AutomationProperties.SetAutomationId(visual, $"PdfLayOutCell_{cell.Row}_{cell.Column}");
            AutomationProperties.SetName(visual, cell.Label);
            visual.DragOver += Cell_DragOver;
            visual.DragLeave += (_, _) => UpdateCellHighlights();
            visual.Drop += Cell_Drop;
            visual.DragStarting += Cell_DragStarting;
            visual.DropCompleted += (_, _) => _dragContent = null;
            visual.Click += Cell_Click;
            // Button 会处理并捕获指针；从已处理事件中识别拖动，再显式启动系统拖放。
            visual.AddHandler(PointerPressedEvent, new PointerEventHandler(Cell_DragPointerPressed), true);
            visual.AddHandler(PointerMovedEvent, new PointerEventHandler(Cell_DragPointerMoved), true);
            visual.AddHandler(PointerReleasedEvent, new PointerEventHandler(Cell_DragPointerEnded), true);
            visual.PointerCanceled += Cell_DragPointerEnded;
            visual.PointerCaptureLost += Cell_DragPointerEnded;
            // Button 会处理部分手势事件；仍接收双击，单击只改变选择。
            visual.AddHandler(DoubleTappedEvent, new DoubleTappedEventHandler(Cell_DoubleTapped), true);
            visual.ContextRequested += Cell_ContextRequested;
            ToolTipService.SetToolTip(visual, "单击选中；双击选择文件；拖动到其他容器复制；右键选页或清空");
            visual.Clip = new RectangleGeometry { Rect = new(0, 0, MillimetersToDip(bounds.Bounds.Width), MillimetersToDip(bounds.Bounds.Height)) };
            Grid.SetRow(visual, cell.Row);
            Grid.SetColumn(visual, cell.Column);
            var canvas = (Canvas)visual.FindName("CellCanvas");
            var image = (Image)visual.FindName("CellImage");
            canvas.Clip = new RectangleGeometry { Rect = new(0, 0, MillimetersToDip(bounds.Bounds.Width), MillimetersToDip(bounds.Bounds.Height)) };
            if (placements.TryGetValue((cell.Row, cell.Column), out var placement))
            {
                canvas.Clip = new RectangleGeometry { Rect = LocalDipRect(placement.ClipBounds, bounds.Bounds) };
                var rect = LocalDipRect(placement.ImageBounds, bounds.Bounds);
                image.Width = rect.Width; image.Height = rect.Height;
                Canvas.SetLeft(image, rect.X); Canvas.SetTop(image, rect.Y);
            }
            _freeVisuals[cell] = visual;
            PreviewGrid.Children.Add(visual);
            _ = RefreshCellVisualAsync(cell);
        }
        UpdateCellHighlights();
    }

    private async void FreeCellChanged(object? sender, PdfLayOutCellViewModel cell)
    {
        UpdateCellHighlights();
        await RefreshCellVisualAsync(cell);
    }

    private async Task RefreshCellVisualAsync(PdfLayOutCellViewModel cell)
    {
        if (!_freeVisuals.TryGetValue(cell, out var visual)) return;
        ((TextBlock)visual.FindName("CellLabel")).Text = ViewModel.IsFreeMode ? cell.Label : "拖入一个 PDF 开始重复拼版";
        AutomationProperties.SetName(visual, cell.Label);
        var image = (Image)visual.FindName("CellImage");
        image.Visibility = BoolToVisibility(ViewModel.ShowPageContent);
        PdfLayOutPagePreview? preview = cell.Preview;
        image.Source = null;
        if (preview is null || !ViewModel.IsFreeMode) return;
        try
        {
            if (!_freeBitmapTasks.TryGetValue(preview, out var task)) _freeBitmapTasks[preview] = task = CreateBitmapAsync(preview);
            BitmapImage bitmap = await task;
            if (_isActive && ReferenceEquals(preview, cell.Preview) && _freeVisuals.TryGetValue(cell, out var current) && ReferenceEquals(visual, current))
                image.Source = bitmap;
        }
        catch (Exception exception)
        {
            if (_isActive && ReferenceEquals(preview, cell.Preview)) ViewModel.ReportInputError($"{cell.PositionText}无法显示预览：{exception.Message}");
        }
    }

    private static async Task<BitmapImage> CreateBitmapAsync(PdfLayOutPagePreview preview, int maximumEdge = 0)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(preview.PngBytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        if (maximumEdge > 0)
        {
            // 真正缩小 PNG 像素尺寸，拖放系统不会再按源图尺寸显示。
            var decoder = await BitmapDecoder.CreateAsync(stream);
            double scale = Math.Min(1, maximumEdge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            using var thumbnail = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(thumbnail, decoder);
            encoder.BitmapTransform.ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale));
            encoder.BitmapTransform.ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale));
            await encoder.FlushAsync();
            thumbnail.Seek(0);
            await bitmap.SetSourceAsync(thumbnail);
        }
        else await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private void UpdateCellHighlights(Button? dropTarget = null)
    {
        foreach (var pair in _freeVisuals)
            ((Border)pair.Value.FindName("CellHighlight")).Visibility = BoolToVisibility(
                ViewModel.IsFreeMode && (ReferenceEquals(pair.Key, ViewModel.SelectedCell) || ReferenceEquals(pair.Value, dropTarget)));
    }

    private void Cell_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanEditCells && sender is Button { Tag: PdfLayOutCellViewModel cell })
            ViewModel.SelectCell(cell);
    }

    private async void Cell_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Button { Tag: PdfLayOutCellViewModel cell }) return;
        e.Handled = true;
        if (ViewModel.IsRepeatMode) { ChooseSource_Click(sender, new RoutedEventArgs()); return; }
        if (!ViewModel.CanEditCells) return;
        ViewModel.SelectCell(cell);
        await ChooseCellFileAsync(cell);
    }

    private void Cell_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { Tag: PdfLayOutCellViewModel cell } visual) return;
        if (!ViewModel.CanEditCells) return;
        e.Handled = true;
        ViewModel.SelectCell(cell);
        if (cell.Content is not { } content) return;
        var panel = new StackPanel { Width = 220, Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = content.Document.FileName, TextWrapping = TextWrapping.Wrap });
        var number = new NumberBox
        {
            Header = $"源页码（共 {content.Document.PageCount} 页）", Minimum = 1, Maximum = content.Document.PageCount,
            Value = content.SourcePageIndex < content.Document.PageCount ? content.SourcePageIndex + 1 : double.NaN, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        AutomationProperties.SetAutomationId(number, "PdfLayOutCellPage");
        number.ValueChanged += (_, args) =>
        {
            if (double.IsFinite(args.NewValue)) ViewModel.SelectCellPage(cell, (int)Math.Round(args.NewValue) - 1);
        };
        panel.Children.Add(number);
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
        var flyout = new Flyout { Content = panel, FlyoutPresenterStyle = presenterStyle };
        var replace = new Button { Content = "替换文件", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(replace, "PdfLayOutCellReplace");
        replace.Click += async (_, _) => { flyout.Hide(); await ChooseCellFileAsync(cell); };
        var clear = new Button { Content = "清空容器", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(clear, "PdfLayOutCellClear");
        clear.Click += (_, _) => { flyout.Hide(); ViewModel.ClearCell(cell); };
        panel.Children.Add(replace); panel.Children.Add(clear);
        flyout.ShowAt(visual);
    }

    private async Task ChooseCellFileAsync(PdfLayOutCellViewModel cell)
    {
        if (!ViewModel.CanEditCells || _isPickingFile) return;
        long revision = ViewModel.ModeRevision;
        _isPickingFile = true;
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            picker.FileTypeFilter.Add(".pdf");
            var result = await picker.PickSingleFileAsync();
            if (result is not null && _isActive && ViewModel.CanEditCells && revision == ViewModel.ModeRevision)
                await ViewModel.LoadCellAsync(cell, result.Path);
        }
        catch (Exception exception) { ViewModel.ReportInputError($"选择 PDF 失败：{exception.Message}"); }
        finally { _isPickingFile = false; }
    }

    private void Cell_DragPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        _cellDragPress = null;
        if (_isCellDragActive || !ViewModel.CanEditCells || _isPickingFile ||
            sender is not Button { Tag: PdfLayOutCellViewModel { Content: not null } } source) return;
        var point = args.GetCurrentPoint(source);
        if (point.Properties.IsLeftButtonPressed)
            _cellDragPress = (source, args.Pointer.PointerId, point.Position);
    }

    private async void Cell_DragPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_isCellDragActive || _cellDragPress is not { } press || !ReferenceEquals(sender, press.Source) ||
            args.Pointer.PointerId != press.PointerId) return;
        var point = args.GetCurrentPoint(press.Source);
        if (!point.Properties.IsLeftButtonPressed || !ViewModel.CanEditCells || _isPickingFile)
        { _cellDragPress = null; return; }
        if (Math.Abs(point.Position.X - press.Position.X) < CellDragThreshold &&
            Math.Abs(point.Position.Y - press.Position.Y) < CellDragThreshold) return;

        _cellDragPress = null;
        _isCellDragActive = true;
        args.Handled = true;
        try
        {
            // 释放 Button 的按下状态，避免松开鼠标时再触发一次 Click。
            press.Source.ReleasePointerCaptures();
            await press.Source.StartDragAsync(point);
        }
        catch (Exception exception)
        { if (_isActive) ViewModel.ReportInputError($"无法拖动容器：{exception.Message}"); }
        finally
        {
            _isCellDragActive = false;
            _dragContent = null;
            UpdateCellHighlights();
        }
    }

    private void Cell_DragPointerEnded(object sender, PointerRoutedEventArgs args)
    {
        if (_cellDragPress is { } press && args.Pointer.PointerId == press.PointerId) _cellDragPress = null;
    }

    private async void Cell_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (!ViewModel.CanEditCells || _isPickingFile || sender is not Button { Tag: PdfLayOutCellViewModel { Content: { } content } cell })
        { args.Cancel = true; return; }
        ViewModel.SelectCell(cell);
        _dragContent = content;
        args.Data.SetData(CellDragFormat, _dragSession);
        args.Data.RequestedOperation = DataPackageOperation.Copy;
        // 不使用整个容器的默认截图；没有页面预览时也只显示系统的小图标。
        args.DragUI.SetContentFromDataPackage();
        if (cell.Preview is not { } preview) return;
        long revision = ViewModel.ModeRevision;
        var deferral = args.GetDeferral();
        try
        {
            BitmapImage thumbnail = await CreateBitmapAsync(preview, maximumEdge: 120);
            if (!_isActive || revision != ViewModel.ModeRevision) { args.Cancel = true; return; }
            args.DragUI.SetContentFromBitmapImage(thumbnail, new(0, 0));
        }
        catch (Exception exception)
        {
            // 缩略图失败不影响复制，保留上面设置的系统图标。
            System.Diagnostics.Debug.WriteLine($"拖动缩略图不可用：{exception.Message}");
        }
        finally { deferral.Complete(); }
    }

    private void Cell_DragOver(object sender, DragEventArgs args)
    {
        args.Handled = true;
        bool accepted = ViewModel.CanEditCells && !_isPickingFile &&
            (args.DataView.Contains(StandardDataFormats.StorageItems) || args.DataView.Contains(CellDragFormat));
        args.AcceptedOperation = accepted ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (accepted)
        {
            args.DragUIOverride.Caption = "复制到此格";
            UpdateCellHighlights((Button)sender);
        }
    }

    private async void Cell_Drop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        args.AcceptedOperation = DataPackageOperation.None;
        if (!ViewModel.CanEditCells || _isPickingFile || sender is not Button { Tag: PdfLayOutCellViewModel cell }) return;
        long revision = ViewModel.ModeRevision;
        var deferral = args.GetDeferral();
        try
        {
            if (args.DataView.Contains(CellDragFormat))
            {
                if (await args.DataView.GetDataAsync(CellDragFormat) is string session && session == _dragSession && _dragContent is { } content &&
                    _isActive && revision == ViewModel.ModeRevision)
                { ViewModel.CopyToCell(content, cell); args.AcceptedOperation = DataPackageOperation.Copy; }
            }
            else if (args.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await args.DataView.GetStorageItemsAsync();
                if (items.Count != 1 || items[0] is not StorageFile file || !string.Equals(file.FileType, ".pdf", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(file.Path))
                    ViewModel.ReportInputError("每个容器只能接收一个本地 PDF 文件。");
                else if (_isActive && ViewModel.CanEditCells && revision == ViewModel.ModeRevision)
                { await ViewModel.LoadCellAsync(cell, file.Path); args.AcceptedOperation = DataPackageOperation.Copy; }
            }
        }
        catch (Exception exception) { ViewModel.ReportInputError($"放入文件失败：{exception.Message}"); }
        finally { UpdateCellHighlights(); deferral.Complete(); }
    }

    private async Task ShowLayoutChangeAsync()
    {
        if (!_isActive || _isManagingPreset || ViewModel.PendingLayoutChange is not { } request) return;
        await RunPresetDialogAsync(async () =>
        {
            _presetDialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "部分容器将被移除",
                Content = "以下容器中的内容将被清空：\n" + string.Join("\n", request.RemovedContents.Select(c => $"第 {c.Row + 1} 行第 {c.Column + 1} 列 · {c.Document.FileName}")),
                PrimaryButtonText = "清空并应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            };
            bool accepted = await _presetDialog.ShowAsync() == ContentDialogResult.Primary;
            await ViewModel.ResolveLayoutChangeAsync(request, _isActive && accepted);
        });
    }
}
