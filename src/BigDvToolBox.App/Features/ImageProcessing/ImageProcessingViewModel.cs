using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace BigDvToolBox_App.Features.ImageProcessing;

internal sealed partial class ImageProcessingViewModel : ObservableObject
{
    private readonly IImageResizeService _resizeService;
    private readonly IImageProcessingSettingsStore _settingsStore;
    private CancellationTokenSource? _processingCancellation;

    internal ImageProcessingViewModel()
        : this(
            new ImageResizeService(),
            new JsonImageProcessingSettingsStore())
    {
    }

    internal ImageProcessingViewModel(
        IImageResizeService resizeService,
        IImageProcessingSettingsStore settingsStore)
    {
        _resizeService = resizeService;
        _settingsStore = settingsStore;

        ImageProcessingDefaults defaults = settingsStore.Load();
        TargetLongEdge = defaults.LongEdge > 0
            ? defaults.LongEdge
            : ImageProcessingFileRules.DefaultLongEdge;
        ConflictPolicyIndex = defaults.ConflictPolicy == ImageConflictPolicy.Overwrite
            ? 0
            : 1;

        if (!string.IsNullOrWhiteSpace(defaults.OutputDirectory))
        {
            if (Directory.Exists(defaults.OutputDirectory))
            {
                OutputDirectory = defaults.OutputDirectory;
            }
            else
            {
                BatchStatus = "已保存的默认输出目录不存在，本次将使用源文件目录。";
                BatchSeverity = ImageStatusSeverity.Warning;
            }
        }
    }

    internal ObservableCollection<ImageFileItem> Files { get; } = [];

    internal bool CanEditFiles => !IsProcessing;

    internal bool CanRemoveFiles => !IsProcessing && SelectedItemCount > 0;

    internal bool CanConfigure => !IsProcessing;

    internal bool CanStart =>
        !IsProcessing &&
        Files.Count > 0 &&
        TargetLongEdge > 0;

    internal bool CanCancel => IsProcessing;

    internal bool IsEmpty => Files.Count == 0;

    internal bool CanUseJpegQuality =>
        !IsProcessing &&
        (OutputFormatIndex == (int)ImageOutputFormat.Jpeg ||
         (OutputFormatIndex == (int)ImageOutputFormat.Original &&
          Files.Any(item => ImageProcessingFileRules.IsJpeg(item.FilePath))));

    internal string JpegQualityDescription =>
        OutputFormatIndex == (int)ImageOutputFormat.Png
            ? "PNG 为无损格式，不使用画质参数。"
            : OutputFormatIndex == (int)ImageOutputFormat.Original
                ? "仅应用于按原格式输出的 JPG/JPEG 图片。"
                : "数值越高，画质和文件体积通常越大。";

    internal string OutputDirectoryDisplay =>
        string.IsNullOrWhiteSpace(OutputDirectory)
            ? "与每个源文件相同"
            : OutputDirectory;

    internal ImageConflictPolicy ConflictPolicy =>
        ConflictPolicyIndex == 0
            ? ImageConflictPolicy.Overwrite
            : ImageConflictPolicy.AddSuffix;

    internal string OutputLocationSummary =>
        string.IsNullOrWhiteSpace(OutputDirectory)
            ? "各图片的源文件目录"
            : OutputDirectory;

    [ObservableProperty]
    public partial int TargetLongEdge { get; set; } =
        ImageProcessingFileRules.DefaultLongEdge;

    [ObservableProperty]
    public partial int JpegQuality { get; set; } =
        ImageProcessingFileRules.DefaultJpegQuality;

    [ObservableProperty]
    public partial int OutputFormatIndex { get; set; }

    [ObservableProperty]
    public partial string? OutputDirectory { get; set; }

    [ObservableProperty]
    public partial int ConflictPolicyIndex { get; set; } = 1;

    [ObservableProperty]
    public partial int SelectedItemCount { get; set; }

    [ObservableProperty]
    public partial bool IsProcessing { get; set; }

    [ObservableProperty]
    public partial double OverallProgress { get; set; }

    [ObservableProperty]
    public partial string BatchStatus { get; set; } = "请选择或拖放图片。";

    [ObservableProperty]
    public partial ImageStatusSeverity BatchSeverity { get; set; } =
        ImageStatusSeverity.Informational;

    [ObservableProperty]
    public partial bool IsBatchStatusOpen { get; set; } = true;

    internal int AddFiles(IEnumerable<string> filePaths)
    {
        if (!CanEditFiles)
        {
            return 0;
        }

        var existingPaths = Files
            .Select(item => item.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0;
        int skipped = 0;
        foreach (string candidate in filePaths)
        {
            if (!ImageProcessingFileRules.IsSupportedImageFile(candidate))
            {
                skipped++;
                continue;
            }

            string fullPath = Path.GetFullPath(candidate);
            if (!existingPaths.Add(fullPath))
            {
                skipped++;
                continue;
            }

            Files.Add(new ImageFileItem(fullPath));
            added++;
        }

        BatchStatus = skipped == 0
            ? $"已添加 {added} 张图片，共 {Files.Count} 张。"
            : $"已添加 {added} 张图片，忽略 {skipped} 个不支持或重复的文件。";
        BatchSeverity = skipped > 0
            ? ImageStatusSeverity.Warning
            : ImageStatusSeverity.Informational;
        IsBatchStatusOpen = true;
        NotifyStateChanged();
        return added;
    }

    internal void RemoveFiles(IEnumerable<ImageFileItem> selectedFiles)
    {
        if (!CanEditFiles)
        {
            return;
        }

        foreach (ImageFileItem item in selectedFiles.Distinct().ToArray())
        {
            Files.Remove(item);
        }

        SelectedItemCount = 0;
        BatchStatus = Files.Count == 0
            ? "请选择或拖放图片。"
            : $"列表中剩余 {Files.Count} 张图片。";
        BatchSeverity = ImageStatusSeverity.Informational;
        IsBatchStatusOpen = true;
        NotifyStateChanged();
    }

    internal void ClearFiles()
    {
        if (!CanEditFiles)
        {
            return;
        }

        Files.Clear();
        SelectedItemCount = 0;
        OverallProgress = 0;
        BatchStatus = "请选择或拖放图片。";
        BatchSeverity = ImageStatusSeverity.Informational;
        IsBatchStatusOpen = true;
        NotifyStateChanged();
    }

    internal void SetOutputDirectory(string? directory)
    {
        if (!CanConfigure)
        {
            return;
        }

        OutputDirectory = string.IsNullOrWhiteSpace(directory)
            ? null
            : Path.GetFullPath(directory);
    }

    internal void SaveLongEdgeDefault() =>
        SaveDefault(
            () => _settingsStore.SaveLongEdge(TargetLongEdge),
            $"默认长边已设置为 {TargetLongEdge}px。");

    internal void SaveOutputDirectoryDefault() =>
        SaveDefault(
            () => _settingsStore.SaveOutputDirectory(OutputDirectory),
            string.IsNullOrWhiteSpace(OutputDirectory)
                ? "默认输出位置已设置为源文件目录。"
                : "默认输出目录已保存。");

    internal void SaveConflictPolicyDefault() =>
        SaveDefault(
            () => _settingsStore.SaveConflictPolicy(ConflictPolicy),
            ConflictPolicy == ImageConflictPolicy.Overwrite
                ? "默认重名策略已设置为覆盖。"
                : "默认重名策略已设置为添加后缀。");

    internal async Task<ImageBatchSummary> StartProcessingAsync()
    {
        if (!CanStart)
        {
            return default;
        }

        ImageFileItem[] files = [.. Files];
        foreach (ImageFileItem file in files)
        {
            file.SetWaiting();
        }

        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new ImageResizeOptions(
            TargetLongEdge,
            JpegQuality,
            (ImageOutputFormat)OutputFormatIndex,
            ConflictPolicy);
        ImageFileItem[] processingOrder = OrderForSafeOverwrite(
            files,
            options.OutputFormat);

        _processingCancellation = new CancellationTokenSource();
        CancellationToken token = _processingCancellation.Token;
        IsProcessing = true;
        OverallProgress = 0;
        BatchStatus = $"正在处理 0/{files.Length}…";
        BatchSeverity = ImageStatusSeverity.Informational;
        IsBatchStatusOpen = true;
        NotifyStateChanged();

        int succeeded = 0;
        int failed = 0;
        int cancelled = 0;
        int processed = 0;
        try
        {
            for (int index = 0; index < processingOrder.Length; index++)
            {
                ImageFileItem file = processingOrder[index];
                if (token.IsCancellationRequested)
                {
                    file.SetCancelled();
                    cancelled++;
                    processed++;
                    OverallProgress = processed * 100d / files.Length;
                    continue;
                }

                file.SetProcessing();
                try
                {
                    string outputPath = ImageProcessingFileRules.ResolveOutputPath(
                        file.FilePath,
                        OutputDirectory,
                        options.OutputFormat,
                        ConflictPolicy,
                        reservedPaths);
                    ImageResizeResult result = await _resizeService.ResizeAsync(
                        file.FilePath,
                        outputPath,
                        options,
                        token);
                    file.SetCompleted(result);
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    file.SetCancelled();
                    cancelled++;
                }
                catch (Exception exception)
                {
                    file.SetFailed(exception);
                    failed++;
                }

                processed++;
                OverallProgress = processed * 100d / files.Length;
                BatchStatus = token.IsCancellationRequested
                    ? $"正在取消，已处理 {processed}/{files.Length}…"
                    : $"正在处理 {processed}/{files.Length}…";
            }
        }
        finally
        {
            IsProcessing = false;
            _processingCancellation.Dispose();
            _processingCancellation = null;
            NotifyStateChanged();
        }

        var summary = new ImageBatchSummary(
            files.Length,
            succeeded,
            failed,
            cancelled);
        BatchStatus = cancelled > 0
            ? $"处理已取消：成功 {succeeded}，失败 {failed}，取消 {cancelled}。"
            : $"处理完成：成功 {succeeded}，失败 {failed}。";
        BatchSeverity = failed > 0
            ? ImageStatusSeverity.Warning
            : cancelled > 0
                ? ImageStatusSeverity.Informational
                : ImageStatusSeverity.Success;
        IsBatchStatusOpen = true;
        return summary;
    }

    internal void Cancel()
    {
        if (!IsProcessing)
        {
            return;
        }

        BatchStatus = "正在取消；当前图片完成解码阶段后停止。";
        BatchSeverity = ImageStatusSeverity.Informational;
        IsBatchStatusOpen = true;
        _processingCancellation?.Cancel();
    }

    partial void OnTargetLongEdgeChanged(int value) =>
        OnPropertyChanged(nameof(CanStart));

    partial void OnJpegQualityChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 100);
        if (value != clamped)
        {
            JpegQuality = clamped;
        }
    }

    partial void OnOutputFormatIndexChanged(int value)
    {
        if (value is < 0 or > 2)
        {
            OutputFormatIndex = 0;
            return;
        }

        OnPropertyChanged(nameof(CanUseJpegQuality));
        OnPropertyChanged(nameof(JpegQualityDescription));
    }

    partial void OnOutputDirectoryChanged(string? value)
    {
        OnPropertyChanged(nameof(OutputDirectoryDisplay));
        OnPropertyChanged(nameof(OutputLocationSummary));
    }

    partial void OnConflictPolicyIndexChanged(int value)
    {
        if (value is < 0 or > 1)
        {
            ConflictPolicyIndex = 1;
            return;
        }

        OnPropertyChanged(nameof(ConflictPolicy));
    }

    partial void OnSelectedItemCountChanged(int value) =>
        OnPropertyChanged(nameof(CanRemoveFiles));

    partial void OnIsProcessingChanged(bool value) =>
        NotifyStateChanged();

    private void SaveDefault(Action save, string successMessage)
    {
        if (!CanConfigure)
        {
            return;
        }

        try
        {
            save();
            BatchStatus = successMessage;
            BatchSeverity = ImageStatusSeverity.Success;
        }
        catch (Exception exception)
        {
            BatchStatus = $"保存默认值失败：{exception.Message}";
            BatchSeverity = ImageStatusSeverity.Error;
        }

        IsBatchStatusOpen = true;
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanEditFiles));
        OnPropertyChanged(nameof(CanRemoveFiles));
        OnPropertyChanged(nameof(CanConfigure));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanUseJpegQuality));
    }

    private ImageFileItem[] OrderForSafeOverwrite(
        ImageFileItem[] files,
        ImageOutputFormat outputFormat)
    {
        if (ConflictPolicy != ImageConflictPolicy.Overwrite)
        {
            return files;
        }

        var outputTargets = files
            .Select(file => ImageProcessingFileRules.ResolveOutputPath(
                file.FilePath,
                OutputDirectory,
                outputFormat,
                ImageConflictPolicy.Overwrite))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Consume a source before another item can overwrite that same path.
        return
        [
            .. files
                .Select((file, index) => (file, index))
                .OrderByDescending(item =>
                    outputTargets.Contains(item.file.FilePath))
                .ThenBy(item => item.index)
                .Select(item => item.file),
        ];
    }
}
