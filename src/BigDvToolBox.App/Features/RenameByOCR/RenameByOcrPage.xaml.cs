using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BigDvToolBox_App.Features.RenameByOCR;

public sealed partial class RenameByOcrPage : Page
{
    private Point _selectionStart;
    private uint? _capturedPointerId;
    private bool _isSelectingFolder;

    internal RenameByOcrViewModel ViewModel { get; } = new();

    public RenameByOcrPage()
    {
        InitializeComponent();
        ContentSelector.SelectedItem = ContentSelector.Items[0];
    }

    private async void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanConfigure || _isSelectingFolder)
        {
            return;
        }

        _isSelectingFolder = true;
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            PickFolderResult? result = await picker.PickSingleFolderAsync();
            if (result is null)
            {
                return;
            }

            await OpenFolderAsync(result.Path);
        }
        catch (Exception exception)
        {
            ViewModel.ReportFolderInputError($"选择文件夹失败：{exception.Message}");
        }
        finally
        {
            _isSelectingFolder = false;
        }
    }

    private void PreviewPanel_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        bool canDrop = ViewModel.CanConfigure && !_isSelectingFolder &&
            e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = canDrop ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (canDrop)
        {
            e.DragUIOverride.Caption = "加载一个文件夹中的 PDF 或图片";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
    }

    private async void PreviewPanel_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (!ViewModel.CanConfigure || _isSelectingFolder ||
            !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        _isSelectingFolder = true;
        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            if (items.Count != 1 || items[0] is not StorageFolder folder ||
                string.IsNullOrWhiteSpace(folder.Path))
            {
                ViewModel.ReportFolderInputError("请每次拖入一个本地文件夹，不支持直接拖入文件或多个文件夹。");
                return;
            }

            // Reading the drag data is asynchronous; recheck before replacing the current folder.
            if (!ViewModel.CanConfigure)
            {
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            await OpenFolderAsync(folder.Path);
        }
        catch (Exception exception)
        {
            ViewModel.ReportFolderInputError($"无法读取拖入的文件夹：{exception.Message}");
        }
        finally
        {
            _isSelectingFolder = false;
            deferral.Complete();
        }
    }

    private async Task OpenFolderAsync(string folderPath)
    {
        await ViewModel.LoadFolderAsync(folderPath);
        await RefreshPreviewImageAsync();
        UpdateSelectionRectangle();
        ContentSelector.SelectedItem = ContentSelector.Items[0];
        ShowSelectedPanel();
    }

    private async void PreviousFile_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SelectPreviousAsync();
        await RefreshPreviewImageAsync();
    }

    private async void NextFile_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.SelectNextAsync();
        await RefreshPreviewImageAsync();
    }

    private async void PdfDpi_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!IsLoaded || !ViewModel.HasFiles || double.IsNaN(args.NewValue))
        {
            return;
        }

        ViewModel.PdfDpi = (int)args.NewValue;
        await ViewModel.LoadCurrentPreviewAsync();
        await RefreshPreviewImageAsync();
    }

    private async void GeneratePreview_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.GenerateRenamePreviewAsync();
        if (ViewModel.HasResults)
        {
            ContentSelector.SelectedItem = ContentSelector.Items[1];
            ShowSelectedPanel();
        }
    }

    private async void ApplyRenames_Click(object sender, RoutedEventArgs e)
    {
        int renameCount = ViewModel.Results.Count(item => item.CanRename);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认批量重命名",
            Content = $"将重命名 {renameCount} 个文件。识别失败和无需修改的项目会被跳过。",
            PrimaryButtonText = "重命名",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.ApplyRenamesAsync();
        await RefreshPreviewImageAsync();
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedFileName = $"OCR结果_{DateTime.Now:yyyyMMdd_HHmmss}",
        };
        picker.FileTypeChoices.Add("CSV 文件", [".csv"]);
        PickFileResult? result = await picker.PickSaveFileAsync();
        if (result is not null)
        {
            await ViewModel.ExportCsvAsync(result.Path);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    private void ContentSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ShowSelectedPanel();

    private void ShowSelectedPanel()
    {
        bool showResults = ReferenceEquals(ContentSelector.SelectedItem, ContentSelector.Items[1]);
        PreviewPanel.Visibility = showResults ? Visibility.Collapsed : Visibility.Visible;
        ResultsPanel.Visibility = showResults ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectionCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.PreviewPixelWidth <= 0 || ViewModel.PreviewPixelHeight <= 0)
        {
            return;
        }

        _selectionStart = e.GetCurrentPoint(SelectionCanvas).Position;
        _capturedPointerId = e.Pointer.PointerId;
        SelectionCanvas.CapturePointer(e.Pointer);
        SetSelectionRectangle(_selectionStart, _selectionStart);
        e.Handled = true;
    }

    private void SelectionCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointerId != e.Pointer.PointerId)
        {
            return;
        }

        SetSelectionRectangle(_selectionStart, e.GetCurrentPoint(SelectionCanvas).Position);
        e.Handled = true;
    }

    private void SelectionCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointerId != e.Pointer.PointerId)
        {
            return;
        }

        Point end = e.GetCurrentPoint(SelectionCanvas).Position;
        SetSelectionRectangle(_selectionStart, end);
        SelectionCanvas.ReleasePointerCapture(e.Pointer);
        _capturedPointerId = null;

        double left = Math.Min(_selectionStart.X, end.X);
        double top = Math.Min(_selectionStart.Y, end.Y);
        double width = Math.Abs(end.X - _selectionStart.X);
        double height = Math.Abs(end.Y - _selectionStart.Y);
        ViewModel.SetSelectedRegion(new NormalizedOcrRegion(
            left / SelectionCanvas.ActualWidth,
            top / SelectionCanvas.ActualHeight,
            width / SelectionCanvas.ActualWidth,
            height / SelectionCanvas.ActualHeight));
        e.Handled = true;
    }

    private void SelectionCanvas_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        SelectionCanvas.ReleasePointerCaptures();
        _capturedPointerId = null;
    }

    private void RegionNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!IsLoaded)
        {
            return;
        }

        ViewModel.SetSelectedRegion(new NormalizedOcrRegion(
            SafePercent(RegionXBox.Value),
            SafePercent(RegionYBox.Value),
            SafePercent(RegionWidthBox.Value),
            SafePercent(RegionHeightBox.Value)));
        UpdateSelectionRectangle();
    }

    private async Task RefreshPreviewImageAsync()
    {
        byte[]? bytes = ViewModel.CurrentPreviewBytes;
        if (bytes is null)
        {
            PreviewImage.Source = null;
            return;
        }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        PreviewImage.Source = image;
    }

    private void UpdateSelectionRectangle()
    {
        if (!ViewModel.SelectedRegion.IsValid ||
            ViewModel.PreviewPixelWidth <= 0 ||
            ViewModel.PreviewPixelHeight <= 0)
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        NormalizedOcrRegion region = ViewModel.SelectedRegion;
        Canvas.SetLeft(SelectionRectangle, region.X * ViewModel.PreviewPixelWidth);
        Canvas.SetTop(SelectionRectangle, region.Y * ViewModel.PreviewPixelHeight);
        SelectionRectangle.Width = region.Width * ViewModel.PreviewPixelWidth;
        SelectionRectangle.Height = region.Height * ViewModel.PreviewPixelHeight;
        SelectionRectangle.Visibility = Visibility.Visible;
    }

    private void SetSelectionRectangle(Point first, Point second)
    {
        double left = Math.Clamp(Math.Min(first.X, second.X), 0, SelectionCanvas.ActualWidth);
        double top = Math.Clamp(Math.Min(first.Y, second.Y), 0, SelectionCanvas.ActualHeight);
        double right = Math.Clamp(Math.Max(first.X, second.X), 0, SelectionCanvas.ActualWidth);
        double bottom = Math.Clamp(Math.Max(first.Y, second.Y), 0, SelectionCanvas.ActualHeight);
        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = right - left;
        SelectionRectangle.Height = bottom - top;
        SelectionRectangle.Visibility = Visibility.Visible;
    }

    private static double SafePercent(double value) =>
        double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 100) / 100d;
}
