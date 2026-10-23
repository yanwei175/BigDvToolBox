using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.Storage.Pickers;
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BigDvToolBox_App.Features.PdfLayOut;

public sealed partial class PdfLayOutPage : Page
{
    private readonly List<Image> _pageImages = [];
    private BitmapImage? _pageImage;
    private long _imageRequestId;
    private bool _isActive;
    private bool _isPickingFile;
    private bool _isManagingPreset;
    private ContentDialog? _presetDialog;
    // 纯视图状态：ScrollViewer 的缩放和滚动位置不进入毫米版式或输出任务。
    private const double PreviewPadding = 24;
    private const double PreviewZoomStep = 1.2;
    private bool _fitPreviewToWindow = true;
    private bool _previewFitQueued;
    private float _fittedPreviewZoom = 1;
    private uint? _panPointerId;
    private Point _panStart;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private InputCursor? _panCursor;

    public PdfLayOutViewModel ViewModel { get; }

    public PdfLayOutPage()
    {
        var documentService = new WindowsPdfLayOutDocumentService();
        var generator = new PdfSharpLayOutPdfGenerator();
        var exportService = new PdfSharpLayOutExportService(generator);
        var printService = new WindowsPdfLayOutPrintService(generator, new WindowsPdfLayOutPrintBackend(
            () => Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId), () => PrintCanvas));
        ViewModel = new(documentService, exportService, printService, documentService, new JsonPdfLayOutPresetStore());
        InitializeComponent();
        DataContext = ViewModel;
        // ScrollViewer 内部可能已处理指针事件，中键拖动仍需接收它们。
        PreviewHost.AddHandler(PointerPressedEvent, new PointerEventHandler(PreviewHost_PointerPressed), true);
        PreviewHost.AddHandler(PointerMovedEvent, new PointerEventHandler(PreviewHost_PointerMoved), true);
        PreviewHost.AddHandler(PointerReleasedEvent, new PointerEventHandler(PreviewHost_PointerReleased), true);
        PreviewHost.PointerCanceled += PreviewHost_PointerCanceled;
        PreviewHost.PointerCaptureLost += PreviewHost_PointerCanceled;
    }

    // 所有毫米尺寸统一换算为 DIP；ScrollViewer 仅对最终视图做光学缩放。
    public static double MillimetersToDip(double value) =>
        double.IsFinite(value) ? Math.Max(0, value) * 96 / 25.4 : 0;

    public static bool Not(bool value) => !value;
    public static Visibility BoolToVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isActive) return;
        _isActive = true;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.FreeCellChanged += FreeCellChanged;
        UpdatePreviewGrid();
        UpdatePreviewZoomControls();
        QueuePreviewFit();
        await ViewModel.InitializePresetsAsync();
        if (!_isActive) return;
        if (ViewModel.PagePreview is not null)
            await RefreshPreviewImageAsync();
        else if (ViewModel.RefreshPreviewCommand.CanExecute(null))
            await ViewModel.RefreshPreviewCommand.ExecuteAsync(null);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _isActive = false;
        if (ViewModel.PendingLayoutChange is { } pending) _ = ViewModel.ResolveLayoutChangeAsync(pending, false);
        _presetDialog?.Hide();
        EndPreviewPan();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.FreeCellChanged -= FreeCellChanged;
        _freeBitmapTasks.Clear();
        Interlocked.Increment(ref _imageRequestId);
        ViewModel.LoadDocumentCommand.Cancel();
        ViewModel.ExportPdfCommand.Cancel();
        ViewModel.PrintCommand.Cancel();
        ViewModel.StopPreview();
        _pageImage = null;
        UpdateImageSources();
    }

    private async void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanManagePresets) return;
        await RunPresetDialogAsync(async () =>
        {
            string suggestion = "新配置";
            for (int suffix = 2; ViewModel.Presets.Any(item => item.Name == suggestion); suffix++)
                suggestion = $"新配置 {suffix}";
            string? name = await AskPresetNameAsync("新增拼版配置", suggestion, null);
            if (name is not null && _isActive && ViewModel.CanManagePresets)
                await ViewModel.AddPresetCommand.ExecuteAsync(name);
        });
    }

    private async void PresetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetSelector.SelectedItem is not PdfLayOutPreset preset || ReferenceEquals(preset, ViewModel.SelectedPreset)) return;
        if (ViewModel.TrySelectPreset(preset)) return;
        // OneWay 绑定不会自动回滚用户刚选的项目，明确恢复当前生效的配置。
        PresetSelector.SelectedItem = ViewModel.SelectedPreset;
        if (!_isActive || !ViewModel.HasUnsavedPresetChanges || !ViewModel.CanManagePresets) return;
        await RunPresetDialogAsync(async () =>
        {
            _presetDialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
                Title = "请先保存当前配置",
                Content = $"“{ViewModel.SelectedPreset?.Name}”有未保存的修改。保存后才能切换到“{preset.Name}”。",
                PrimaryButtonText = "保存并切换", CloseButtonText = "返回编辑",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await _presetDialog.ShowAsync() == ContentDialogResult.Primary && _isActive && ViewModel.CanLoadPreset)
            {
                await ViewModel.SavePresetCommand.ExecuteAsync(null);
                if (_isActive && !ViewModel.HasUnsavedPresetChanges && ViewModel.ErrorMessage is null)
                    ViewModel.TrySelectPreset(preset);
            }
        });
    }

    private async void RenamePreset_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanLoadPreset || ViewModel.SelectedPreset is not { } preset) return;
        await RunPresetDialogAsync(async () =>
        {
            string? name = await AskPresetNameAsync("重命名拼版配置", preset.Name, preset.Id);
            if (name is not null && _isActive && ViewModel.CanLoadPreset && ViewModel.SelectedPreset?.Id == preset.Id)
                await ViewModel.RenamePresetCommand.ExecuteAsync(name);
        });
    }

    private async void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanDeletePreset || ViewModel.SelectedPreset is not { } preset) return;
        await RunPresetDialogAsync(async () =>
        {
            _presetDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = "删除拼版配置",
                Content = $"确定删除“{preset.Name}”吗？删除后将加载默认配置。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await _presetDialog.ShowAsync() == ContentDialogResult.Primary && _isActive &&
                ViewModel.CanDeletePreset && ViewModel.SelectedPreset?.Id == preset.Id)
                await ViewModel.DeletePresetCommand.ExecuteAsync(null);
        });
    }

    private async Task<string?> AskPresetNameAsync(string title, string initialName, Guid? excludedId)
    {
        var nameBox = new TextBox { Header = "配置名称", Text = initialName, MaxLength = 80, MinWidth = 280 };
        AutomationProperties.SetAutomationId(nameBox, "PdfLayOutPresetName");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(nameBox);
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = content,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                string name = JsonPdfLayOutPresetStore.ValidateName(nameBox.Text);
                if (ViewModel.Presets.Any(item => item.Id != excludedId &&
                    string.Equals(item.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("已有同名配置，请使用其他名称。");
            }
            catch (ArgumentException exception)
            {
                error.Text = exception.Message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };
        dialog.Opened += (_, _) => { nameBox.Focus(FocusState.Programmatic); nameBox.SelectAll(); };
        _presetDialog = dialog;
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? nameBox.Text.Trim() : null;
    }

    private async Task RunPresetDialogAsync(Func<Task> action)
    {
        if (_isManagingPreset) return;
        _isManagingPreset = true;
        try { await action(); }
        catch (Exception exception) { ViewModel.ReportInputError($"配置操作失败：{exception.Message}"); }
        finally { _presetDialog = null; _isManagingPreset = false; if (ViewModel.PendingLayoutChange is not null) DispatcherQueue.TryEnqueue(async () => await ShowLayoutChangeAsync()); }
    }

    private async void ChooseSource_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanLoadDocument || _isPickingFile) return;
        if (ViewModel.IsFreeMode && ViewModel.SelectedCell is { } cell) { await ChooseCellFileAsync(cell); return; }
        _isPickingFile = true;
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            picker.FileTypeFilter.Add(".pdf");
            PickFileResult? result = await picker.PickSingleFileAsync();
            if (result is not null && _isActive && ViewModel.CanLoadDocument)
                await ViewModel.LoadDocumentCommand.ExecuteAsync(result.Path);
        }
        catch (Exception exception)
        {
            ViewModel.ReportInputError($"选择 PDF 失败：{exception.Message}");
        }
        finally { _isPickingFile = false; }
    }

    private async void ExportPdf_Click(SplitButton sender, SplitButtonClickEventArgs e)
    {
        if (!ViewModel.CanExport || _isPickingFile) return;
        _isPickingFile = true;
        try
        {
            var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedFileName = ViewModel.ExportSuggestedName,
                DefaultFileExtension = ".pdf",
            };
            picker.FileTypeChoices.Add("PDF 文件", new List<string> { ".pdf" });
            PickFileResult? result = await picker.PickSaveFileAsync();
            if (result is not null && _isActive && ViewModel.CanExport)
                await ViewModel.ExportPdfCommand.ExecuteAsync(result.Path);
        }
        catch (Exception exception)
        {
            ViewModel.ReportInputError($"导出 PDF 失败：{exception.Message}");
        }
        finally { _isPickingFile = false; }
    }

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        bool canDrop = ViewModel.IsRepeatMode && ViewModel.CanLoadDocument && !_isPickingFile &&
            e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = canDrop ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (canDrop)
        {
            e.DragUIOverride.Caption = "加载一个 PDF 文件";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void Files_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (ViewModel.IsFreeMode || !ViewModel.CanLoadDocument || _isPickingFile ||
            !e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            if (items.Count != 1 || items[0] is not StorageFile file ||
                !string.Equals(file.FileType, ".pdf", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(file.Path))
            {
                ViewModel.ReportInputError("请每次拖入一个本地 PDF 文件。");
                return;
            }
            if (!_isActive || !ViewModel.CanLoadDocument) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            await ViewModel.LoadDocumentCommand.ExecuteAsync(file.Path);
        }
        catch (Exception exception)
        {
            ViewModel.ReportInputError($"无法加载拖入的 PDF：{exception.Message}");
        }
        finally { deferral.Complete(); }
    }

    private async void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.PendingLayoutChange)) { await ShowLayoutChangeAsync(); return; }
        if (e.PropertyName == nameof(ViewModel.CurrentLayout))
            UpdatePreviewGrid();
        else if (!ViewModel.HasDocument && (e.PropertyName is nameof(ViewModel.SheetWidth) or nameof(ViewModel.SheetHeight)))
            UpdatePreviewGrid();
        else if (e.PropertyName == nameof(ViewModel.ShowPageContent))
            UpdateImageSources();
        else if (e.PropertyName == nameof(ViewModel.PagePreview))
            await RefreshPreviewImageAsync();
    }

    private async Task RefreshPreviewImageAsync()
    {
        long requestId = Interlocked.Increment(ref _imageRequestId);
        PdfLayOutPagePreview? preview = ViewModel.PagePreview;
        _pageImage = null;
        UpdateImageSources();
        if (preview is null || !_isActive) return;

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(preview.PngBytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            if (_isActive && requestId == Volatile.Read(ref _imageRequestId) &&
                ReferenceEquals(preview, ViewModel.PagePreview))
            {
                _pageImage = image;
                UpdateImageSources();
            }
        }
        catch (Exception exception)
        {
            if (_isActive && requestId == Volatile.Read(ref _imageRequestId))
                ViewModel.ReportPreviewError($"无法显示页面图像：{exception.Message}");
        }
    }

    private void UpdatePreviewGrid()
    {
        PreviewGrid.Children.Clear();
        PreviewGrid.RowDefinitions.Clear();
        PreviewGrid.ColumnDefinitions.Clear();
        PrintMarksCanvas.Children.Clear();
        _pageImages.Clear();
        _freeVisuals.Clear();

        PdfLayOutSize paperSize = ViewModel.CurrentLayout?.SheetSize ?? new(ViewModel.SheetWidth, ViewModel.SheetHeight);
        double paperWidth = MillimetersToDip(paperSize.Width);
        double paperHeight = MillimetersToDip(paperSize.Height);
        if (PaperSurface.Width != paperWidth || PaperSurface.Height != paperHeight)
            _fitPreviewToWindow = true;
        PaperSurface.Width = paperWidth;
        PaperSurface.Height = paperHeight;
        PreviewContent.Width = paperWidth + PreviewPadding * 2;
        PreviewContent.Height = paperHeight + PreviewPadding * 2;
        QueuePreviewFit();
        PaperSurface.Clip = new RectangleGeometry { Rect = new(0, 0, PaperSurface.Width, PaperSurface.Height) };
        if (ViewModel.CurrentLayout is not { Cells.Count: > 0 } layout)
            return;

        // Grid 保留原生行列间距；大小、位置和单元格均只取解析后的毫米版式。
        PreviewGrid.Width = MillimetersToDip(layout.GridBounds.Width);
        PreviewGrid.Height = MillimetersToDip(layout.GridBounds.Height);
        Canvas.SetLeft(PreviewGrid, MillimetersToDipSigned(layout.GridBounds.X));
        Canvas.SetTop(PreviewGrid, MillimetersToDipSigned(layout.GridBounds.Y));
        PreviewGrid.ColumnSpacing = MillimetersToDip(layout.HorizontalGap);
        PreviewGrid.RowSpacing = MillimetersToDip(layout.VerticalGap);
        for (int row = 0; row < layout.Rows; row++)
            PreviewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(MillimetersToDip(layout.Cells[row * layout.Columns].Bounds.Height)) });
        for (int column = 0; column < layout.Columns; column++)
            PreviewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MillimetersToDip(layout.Cells[column].Bounds.Width)) });

        if (ViewModel.IsFreeMode || ViewModel.SourceDocument is null)
        {
            BuildFreeCells(layout);
            DrawPrintMarks(layout.Marks);
            return;
        }
        _freeVisuals.Clear();
        _freeBitmapTasks.Clear();
        var pageTemplate = (DataTemplate)Resources["RepeatPageTemplate"];
        foreach (PdfLayOutPlacement placement in layout.Placements)
        {
            if (placement.ClipBounds.IsEmpty) continue;
            var cell = (Grid)pageTemplate.LoadContent();
            var canvas = (Canvas)cell.Children[0];
            var image = (Image)canvas.Children[0];
            // 这里仅转换坐标系与显示单位，不再计算缩放、源裁切或最终可见范围。
            canvas.Clip = new RectangleGeometry
            {
                Rect = LocalDipRect(placement.ClipBounds, placement.CellBounds),
            };
            Rect imageBounds = LocalDipRect(placement.ImageBounds, placement.CellBounds);
            image.Width = imageBounds.Width;
            image.Height = imageBounds.Height;
            Canvas.SetLeft(image, imageBounds.X);
            Canvas.SetTop(image, imageBounds.Y);
            Grid.SetRow(cell, placement.Row);
            Grid.SetColumn(cell, placement.Column);
            _pageImages.Add(image);
            PreviewGrid.Children.Add(cell);
        }
        DrawPrintMarks(layout.Marks);
        UpdateImageSources();
    }

    private void DrawPrintMarks(IReadOnlyList<PdfLayOutPrintMark> marks)
    {
        var lineTemplate = (DataTemplate)Resources["PrintMarkLineTemplate"];
        foreach (PdfLayOutPrintMark mark in marks)
        {
            foreach (PdfLayOutLine segment in mark.Lines)
            {
                var line = (Line)lineTemplate.LoadContent();
                line.X1 = MillimetersToDipSigned(segment.Start.X);
                line.Y1 = MillimetersToDipSigned(segment.Start.Y);
                line.X2 = MillimetersToDipSigned(segment.End.X);
                line.Y2 = MillimetersToDipSigned(segment.End.Y);
                line.StrokeThickness = MillimetersToDip(mark.StrokeWidth);
                PrintMarksCanvas.Children.Add(line);
            }
        }
    }

    private static Rect LocalDipRect(PdfLayOutRect rectangle, PdfLayOutRect origin) => new(
        MillimetersToDipSigned(rectangle.X - origin.X), MillimetersToDipSigned(rectangle.Y - origin.Y),
        MillimetersToDip(rectangle.Width), MillimetersToDip(rectangle.Height));

    private void PreviewZoomOut_Click(object sender, RoutedEventArgs e) =>
        ZoomPreview(PreviewScroller.ZoomFactor / PreviewZoomStep);

    private void PreviewZoomIn_Click(object sender, RoutedEventArgs e) =>
        ZoomPreview(PreviewScroller.ZoomFactor * PreviewZoomStep);

    private void PreviewFit_Click(object sender, RoutedEventArgs e)
    {
        EndPreviewPan();
        _fitPreviewToWindow = true;
        QueuePreviewFit();
    }

    private void ZoomPreview(double factor)
    {
        EndPreviewPan();
        _fitPreviewToWindow = false;
        float zoom = (float)Math.Clamp(factor, PreviewScroller.MinZoomFactor, PreviewScroller.MaxZoomFactor);
        double oldZoom = PreviewScroller.ZoomFactor;
        // 工具栏围绕视口中心缩放；Ctrl+滚轮由 ScrollViewer 原生处理，围绕鼠标位置缩放。
        double viewportWidth = PreviewScroller.ViewportWidth, viewportHeight = PreviewScroller.ViewportHeight;
        double centeredX = Math.Max(0, (viewportWidth - PreviewContent.Width * oldZoom) / 2);
        double centeredY = Math.Max(0, (viewportHeight - PreviewContent.Height * oldZoom) / 2);
        double x = (PreviewScroller.HorizontalOffset + viewportWidth / 2 - centeredX) / oldZoom;
        double y = (PreviewScroller.VerticalOffset + viewportHeight / 2 - centeredY) / oldZoom;
        PreviewScroller.ChangeView(
            Math.Max(0, x * zoom - viewportWidth / 2),
            Math.Max(0, y * zoom - viewportHeight / 2), zoom, disableAnimation: true);
    }

    private void PreviewScroller_SizeChanged(object sender, SizeChangedEventArgs e) => QueuePreviewFit();

    private void QueuePreviewFit()
    {
        if (!_fitPreviewToWindow || _previewFitQueued) return;
        _previewFitQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _previewFitQueued = false;
            if (!_isActive || !_fitPreviewToWindow || PreviewScroller.ViewportWidth <= 0 || PreviewScroller.ViewportHeight <= 0)
                return;
            // 预留少量误差，避免缩放后刚好出现滚动条而导致布局反复变化。
            double factor = Math.Min((PreviewScroller.ViewportWidth - 1) / PreviewContent.Width,
                (PreviewScroller.ViewportHeight - 1) / PreviewContent.Height);
            _fittedPreviewZoom = (float)Math.Clamp(factor, PreviewScroller.MinZoomFactor, PreviewScroller.MaxZoomFactor);
            PreviewScroller.ChangeView(0, 0, _fittedPreviewZoom, disableAnimation: true);
        });
    }

    private void PreviewScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_fitPreviewToWindow && Math.Abs(PreviewScroller.ZoomFactor - _fittedPreviewZoom) > 0.0001)
            _fitPreviewToWindow = false;
        UpdatePreviewZoomControls();
    }

    private void UpdatePreviewZoomControls()
    {
        if (PreviewZoomButton is null || PreviewScroller is null) return;
        string percent = $"{PreviewScroller.ZoomFactor * 100:0}%";
        PreviewZoomButton.Content = percent;
        AutomationProperties.SetName(PreviewZoomButton, $"适应窗口，当前缩放 {percent}");
        PreviewZoomOutButton.IsEnabled = PreviewScroller.ZoomFactor > PreviewScroller.MinZoomFactor + 0.0001;
        PreviewZoomInButton.IsEnabled = PreviewScroller.ZoomFactor < PreviewScroller.MaxZoomFactor - 0.0001;
    }

    private void PreviewHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(PreviewHost);
        if (_panPointerId is not null || !point.Properties.IsMiddleButtonPressed || !PreviewHost.CapturePointer(e.Pointer))
            return;
        _fitPreviewToWindow = false;
        _panPointerId = e.Pointer.PointerId;
        _panStart = point.Position;
        _panStartHorizontalOffset = PreviewScroller.HorizontalOffset;
        _panStartVerticalOffset = PreviewScroller.VerticalOffset;
        _panCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        ProtectedCursor = _panCursor;
        PreviewScroller.Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void PreviewHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_panPointerId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(PreviewHost);
        if (!point.Properties.IsMiddleButtonPressed)
        {
            EndPreviewPan();
            return;
        }
        PreviewScroller.ChangeView(
            Math.Clamp(_panStartHorizontalOffset - point.Position.X + _panStart.X, 0, PreviewScroller.ScrollableWidth),
            Math.Clamp(_panStartVerticalOffset - point.Position.Y + _panStart.Y, 0, PreviewScroller.ScrollableHeight),
            null, disableAnimation: true);
        e.Handled = true;
    }

    private void PreviewHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_panPointerId != e.Pointer.PointerId || e.GetCurrentPoint(PreviewHost).Properties.IsMiddleButtonPressed) return;
        EndPreviewPan();
        e.Handled = true;
    }

    private void PreviewHost_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_panPointerId == e.Pointer.PointerId) EndPreviewPan();
    }

    private void EndPreviewPan()
    {
        _panPointerId = null;
        PreviewHost.ReleasePointerCaptures();
        ProtectedCursor = null;
        _panCursor?.Dispose();
        _panCursor = null;
    }

    public static double MillimetersToDipSigned(double value) => double.IsFinite(value) ? value * 96 / 25.4 : 0;

    private void UpdateImageSources()
    {
        foreach (Image image in _pageImages)
        {
            image.Source = _pageImage;
            image.Visibility = BoolToVisibility(ViewModel.ShowPageContent);
        }
    }
}
