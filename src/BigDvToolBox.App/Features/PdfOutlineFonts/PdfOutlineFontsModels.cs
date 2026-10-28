using CommunityToolkit.Mvvm.ComponentModel;

namespace BigDvToolBox_App.Features.PdfOutlineFonts;

internal enum PdfConvertMode
{
    Vector,
    Image,
}

internal enum PdfFileState
{
    Waiting,
    Converting,
    Completed,
    Failed,
    Cancelled,
}

internal enum PdfStatusSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

internal sealed partial class PdfFileItem : ObservableObject
{
    public PdfFileItem(string filePath)
    {
        FilePath = filePath;
    }

    internal Guid? PoolId { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName))]
    public partial string FilePath { get; set; } = string.Empty;

    public string FileName => Path.GetFileName(FilePath);

    [ObservableProperty]
    public partial PdfFileState State { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "等待中";

    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    internal void SetWaiting()
    {
        State = PdfFileState.Waiting;
        Status = "等待中";
        IsIndeterminate = false;
        Progress = 0;
        ErrorMessage = null;
    }

    internal void SetConverting()
    {
        State = PdfFileState.Converting;
        Status = "转换中";
        IsIndeterminate = true;
        Progress = 0;
        ErrorMessage = null;
    }

    internal void SetCompleted()
    {
        State = PdfFileState.Completed;
        Status = "已完成";
        IsIndeterminate = false;
        Progress = 100;
        ErrorMessage = null;
    }

    internal void SetFailed(Exception exception)
    {
        State = PdfFileState.Failed;
        Status = "失败";
        IsIndeterminate = false;
        Progress = 0;
        ErrorMessage = exception.Message;
    }

    internal void SetCancelled()
    {
        State = PdfFileState.Cancelled;
        Status = "已取消";
        IsIndeterminate = false;
        Progress = 0;
        ErrorMessage = null;
    }
}

internal readonly record struct PdfBatchSummary(
    int Total,
    int Succeeded,
    int Failed,
    int Cancelled);

internal static class PdfOutlineFileRules
{
    internal const int MinimumDpi = 72;
    internal const int MaximumDpi = 1200;

    internal static bool IsPdfFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(path) &&
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    internal static string GetOutputPath(string inputPath, string? outputDirectory)
    {
        string directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory()
            : outputDirectory;
        string fileName = $"{Path.GetFileNameWithoutExtension(inputPath)}_outlined.pdf";
        return Path.Combine(directory, fileName);
    }

    internal static int ClampDpi(int dpi) =>
        Math.Clamp(dpi, MinimumDpi, MaximumDpi);

    internal static int ClampThreadCount(int threadCount) =>
        Math.Clamp(threadCount, 1, Math.Max(1, Environment.ProcessorCount));
}
