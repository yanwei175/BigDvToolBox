using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace BigDvToolBox_App.Features.ImageProcessing;

public sealed partial class ImageProcessingPage : Page
{
    internal ImageProcessingViewModel ViewModel { get; } = new();

    public ImageProcessingPage()
    {
        InitializeComponent();
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    internal static InfoBarSeverity ToInfoBarSeverity(
        ImageStatusSeverity severity) =>
        severity switch
        {
            ImageStatusSeverity.Success => InfoBarSeverity.Success,
            ImageStatusSeverity.Warning => InfoBarSeverity.Warning,
            ImageStatusSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(
            XamlRoot.ContentIslandEnvironment.AppWindowId);
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        IReadOnlyList<PickFileResult> results =
            await picker.PickMultipleFilesAsync();
        ViewModel.AddFiles(results.Select(result => result.Path));
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e) =>
        RemoveSelectedFiles();

    private void ClearFiles_Click(object sender, RoutedEventArgs e)
        => ViewModel.ClearFiles();

    private async void ChooseOutputDirectory_Click(
        object sender,
        RoutedEventArgs e)
    {
        var picker = new FolderPicker(
            XamlRoot.ContentIslandEnvironment.AppWindowId);
        PickFolderResult? result = await picker.PickSingleFolderAsync();
        if (result is not null)
        {
            ViewModel.SetOutputDirectory(result.Path);
        }
    }

    private void ResetOutputDirectory_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SetOutputDirectory(null);

    private void SaveLongEdgeDefault_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SaveLongEdgeDefault();

    private void SaveOutputDirectoryDefault_Click(
        object sender,
        RoutedEventArgs e) =>
        ViewModel.SaveOutputDirectoryDefault();

    private void SaveConflictDefault_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SaveConflictPolicyDefault();

    private void JpegQuality_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ViewModel.JpegQuality = (int)Math.Round(e.NewValue);
        }
    }

    private async void StartProcessing_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ConflictPolicy == ImageConflictPolicy.Overwrite)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "确认覆盖已有图片",
                Content =
                    $"将处理 {ViewModel.SelectedForProcessingCount} 张已勾选图片，输出到{ViewModel.OutputLocationSummary}。" +
                    "同名文件会被覆盖，此操作无法撤销。",
                PrimaryButtonText = "覆盖并开始",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        await ViewModel.StartProcessingAsync();
    }

    private void CancelProcessing_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Cancel();

    private void Page_Unloaded(object sender, RoutedEventArgs e) =>
        ViewModel.Cancel();

    private void ProcessingCheckBox_Click(object sender, RoutedEventArgs e) =>
        ViewModel.NotifyProcessingSelectionChanged();

    private void FileList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || !ViewModel.CanStart)
        {
            return;
        }

        RemoveSelectedFiles();
        e.Handled = true;
    }

    private void RemoveSelectedFiles()
    {
        ImageFileItem[] selected = ViewModel.Files
            .Where(item => item.IsSelectedForProcessing)
            .ToArray();
        ViewModel.RemoveFiles(selected);
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
        e.DragUIOverride.Caption = "添加 JPG 或 PNG 图片";
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

        IReadOnlyList<IStorageItem> items =
            await e.DataView.GetStorageItemsAsync();
        ViewModel.AddFiles(
            items.OfType<StorageFile>().Select(file => file.Path));
    }
}
