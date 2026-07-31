using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace BigDvToolBox_App.Features.PdfOutlineFonts;

public sealed partial class PdfOutlineFontsPage : Page
{
    internal PdfOutlineFontsViewModel ViewModel { get; } = new();

    public PdfOutlineFontsPage()
    {
        InitializeComponent();
    }

    public static bool Not(bool value) => !value;

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    internal static InfoBarSeverity ToInfoBarSeverity(PdfStatusSeverity severity) =>
        severity switch
        {
            PdfStatusSeverity.Success => InfoBarSeverity.Success,
            PdfStatusSeverity.Warning => InfoBarSeverity.Warning,
            PdfStatusSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };

    private async void Page_Loaded(object sender, RoutedEventArgs e) =>
        await ViewModel.InitializeAsync();

    private void Page_Unloaded(object sender, RoutedEventArgs e) =>
        ViewModel.Cancel();

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        picker.FileTypeFilter.Add(".pdf");
        IReadOnlyList<PickFileResult> results = await picker.PickMultipleFilesAsync();
        ViewModel.AddFiles(results.Select(result => result.Path));
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedFiles();

    private void ClearFiles_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearFiles();
        FileList.SelectedItems.Clear();
    }

    private async void ChooseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
        PickFolderResult? result = await picker.PickSingleFolderAsync();
        if (result is not null)
        {
            ViewModel.SetOutputDirectory(result.Path);
        }
    }

    private void ResetOutputDirectory_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SetOutputDirectory(null);

    private async void StartConversion_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.StartConversionAsync();

    private void CancelConversion_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Cancel();

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SelectedItemCount = FileList.SelectedItems.Count;

    private void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || !ViewModel.CanRemoveFiles)
        {
            return;
        }

        RemoveSelectedFiles();
        e.Handled = true;
    }

    private void RemoveSelectedFiles()
    {
        PdfFileItem[] selected = FileList.SelectedItems
            .OfType<PdfFileItem>()
            .ToArray();
        ViewModel.RemoveFiles(selected);
        FileList.SelectedItems.Clear();
    }

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanEditFiles ||
            !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "添加 PDF 文件";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void Files_Drop(object sender, DragEventArgs e)
    {
        if (!ViewModel.CanEditFiles ||
            !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        ViewModel.AddFiles(items.OfType<StorageFile>().Select(file => file.Path));
    }
}
