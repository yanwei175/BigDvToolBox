using CommunityToolkit.Mvvm.ComponentModel;

namespace BigDvToolBox_App.Features.ImageProcessing;

internal enum ImageOutputFormat
{
    Original,
    Jpeg,
    Png,
}

internal enum ImageConflictPolicy
{
    Overwrite,
    AddSuffix,
}

internal enum ImageFileState
{
    Waiting,
    Processing,
    Completed,
    Failed,
    Cancelled,
}

internal enum ImageStatusSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

internal readonly record struct ImageResizeOptions(
    int LongEdge,
    int JpegQuality,
    ImageOutputFormat OutputFormat,
    ImageConflictPolicy ConflictPolicy);

internal readonly record struct ImageDimensions(uint Width, uint Height);

internal readonly record struct ImageResizeResult(
    uint Width,
    uint Height,
    string OutputPath);

internal readonly record struct ImageBatchSummary(
    int Total,
    int Succeeded,
    int Failed,
    int Cancelled);

internal sealed partial class ImageFileItem : ObservableObject
{
    internal ImageFileItem(string filePath)
    {
        FilePath = filePath;
    }

    internal string FilePath { get; }

    internal string FileName => Path.GetFileName(FilePath);

    internal string ProcessingSelectionName => $"处理 {FileName}";

    [ObservableProperty]
    public partial bool IsSelectedForProcessing { get; set; } = true;

    [ObservableProperty]
    public partial ImageFileState State { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "等待中";

    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? OutputPath { get; set; }

    internal void SetWaiting()
    {
        State = ImageFileState.Waiting;
        Status = "等待中";
        IsIndeterminate = false;
        Progress = 0;
        ErrorMessage = null;
        OutputPath = null;
    }

    internal void SetProcessing()
    {
        State = ImageFileState.Processing;
        Status = "处理中";
        IsIndeterminate = true;
        Progress = 0;
        ErrorMessage = null;
    }

    internal void SetCompleted(ImageResizeResult result)
    {
        State = ImageFileState.Completed;
        Status = $"已完成（{result.Width} × {result.Height}）";
        IsIndeterminate = false;
        Progress = 100;
        ErrorMessage = null;
        OutputPath = result.OutputPath;
    }

    internal void SetFailed(Exception exception)
    {
        State = ImageFileState.Failed;
        Status = "失败";
        IsIndeterminate = false;
        Progress = 0;
        ErrorMessage = exception.Message;
    }

    internal void SetCancelled()
    {
        State = ImageFileState.Cancelled;
        Status = "已取消";
        IsIndeterminate = false;
        Progress = 0;
        ErrorMessage = null;
    }
}

internal static class ImageProcessingFileRules
{
    internal const int DefaultLongEdge = 1500;
    internal const int DefaultJpegQuality = 85;

    internal static bool IsSupportedImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        string extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsJpeg(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    internal static ImageDimensions CalculateDimensions(
        uint sourceWidth,
        uint sourceHeight,
        int targetLongEdge)
    {
        if (sourceWidth == 0 || sourceHeight == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceWidth),
                "图片尺寸必须大于 0。");
        }

        if (targetLongEdge <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetLongEdge),
                "目标长边必须大于 0。");
        }

        uint sourceLongEdge = Math.Max(sourceWidth, sourceHeight);
        if (sourceLongEdge <= targetLongEdge)
        {
            return new ImageDimensions(sourceWidth, sourceHeight);
        }

        double scale = targetLongEdge / (double)sourceLongEdge;
        uint width = Math.Max(1u, (uint)Math.Round(sourceWidth * scale));
        uint height = Math.Max(1u, (uint)Math.Round(sourceHeight * scale));
        return new ImageDimensions(width, height);
    }

    internal static string GetOutputExtension(
        string inputPath,
        ImageOutputFormat outputFormat) =>
        outputFormat switch
        {
            ImageOutputFormat.Jpeg => ".jpg",
            ImageOutputFormat.Png => ".png",
            _ => Path.GetExtension(inputPath),
        };

    internal static string ResolveOutputPath(
        string inputPath,
        string? outputDirectory,
        ImageOutputFormat outputFormat,
        ImageConflictPolicy conflictPolicy,
        ISet<string>? reservedPaths = null)
    {
        string directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetDirectoryName(inputPath) ??
              throw new InvalidOperationException("无法确定源文件目录。")
            : outputDirectory;
        string extension = GetOutputExtension(inputPath, outputFormat);
        string baseName = Path.GetFileNameWithoutExtension(inputPath);
        string desiredPath = Path.GetFullPath(Path.Combine(directory, baseName + extension));

        if (conflictPolicy == ImageConflictPolicy.Overwrite)
        {
            reservedPaths?.Add(desiredPath);
            return desiredPath;
        }

        string candidate = desiredPath;
        int suffix = 1;
        while (File.Exists(candidate) ||
               (reservedPaths?.Contains(candidate) ?? false))
        {
            candidate = Path.Combine(directory, $"{baseName}_{suffix}{extension}");
            suffix++;
        }

        candidate = Path.GetFullPath(candidate);
        reservedPaths?.Add(candidate);
        return candidate;
    }
}
