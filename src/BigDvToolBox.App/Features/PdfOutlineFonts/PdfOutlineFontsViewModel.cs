using BigDvToolBox_App.Features.FilePool;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace BigDvToolBox_App.Features.PdfOutlineFonts;

internal sealed partial class PdfOutlineFontsViewModel : ObservableObject
{
    private readonly IGhostscriptService _ghostscriptService;
    private CancellationTokenSource? _conversionCancellation;

    internal PdfOutlineFontsViewModel()
        : this(new GhostscriptService())
    {
    }

    internal PdfOutlineFontsViewModel(IGhostscriptService ghostscriptService)
    {
        _ghostscriptService = ghostscriptService;
        int processorCount = Math.Max(1, Environment.ProcessorCount);
        ThreadCounts = [.. Enumerable.Range(1, processorCount)];
        DpiOptions = [150, 200, 300, 600, 1200];
        SelectedThreadCount = Math.Min(2, processorCount);
    }

    internal ObservableCollection<PdfFileItem> Files { get; } = [];

    internal IReadOnlyList<int> ThreadCounts { get; }

    internal IReadOnlyList<int> DpiOptions { get; }

    internal bool IsPlatformSupported => _ghostscriptService.IsSupported;

    internal string PlatformMessage =>
        "Ghostscript 暂无 Windows ARM64 原生库。请安装并运行 BigDvToolBox 的 x64 版本。";

    internal bool IsImageMode => ConvertModeIndex == (int)PdfConvertMode.Image;

    internal bool CanEditFiles => !IsConverting;

    internal bool CanRemoveFiles => !IsConverting && SelectedItemCount > 0;

    internal bool CanConfigure => IsPlatformSupported && !IsConverting;

    internal bool CanConfigureImageMode => CanConfigure && IsImageMode;

    internal bool CanStart =>
        IsPlatformSupported &&
        IsGhostscriptReady &&
        !IsConverting &&
        Files.Count > 0;

    internal bool CanCancel => IsConverting;

    internal bool IsEmpty => Files.Count == 0;

    internal string OutputDirectoryDisplay =>
        string.IsNullOrWhiteSpace(OutputDirectory)
            ? "与每个源文件相同"
            : OutputDirectory;

    [ObservableProperty]
    public partial int ConvertModeIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedThreadCount { get; set; }

    [ObservableProperty]
    public partial int SelectedDpi { get; set; } = 600;

    [ObservableProperty]
    public partial string? OutputDirectory { get; set; }

    [ObservableProperty]
    public partial int SelectedItemCount { get; set; }

    [ObservableProperty]
    public partial bool IsGhostscriptReady { get; set; }

    [ObservableProperty]
    public partial bool IsInitializing { get; set; }

    [ObservableProperty]
    public partial string GhostscriptStatus { get; set; } = "等待初始化 Ghostscript…";

    [ObservableProperty]
    public partial PdfStatusSeverity GhostscriptSeverity { get; set; } = PdfStatusSeverity.Informational;

    [ObservableProperty]
    public partial bool IsConverting { get; set; }

    [ObservableProperty]
    public partial double OverallProgress { get; set; }

    [ObservableProperty]
    public partial string BatchStatus { get; set; } = "就绪";

    [ObservableProperty]
    public partial PdfStatusSeverity BatchSeverity { get; set; } = PdfStatusSeverity.Informational;

    [ObservableProperty]
    public partial bool IsBatchStatusOpen { get; set; } = true;

    internal async Task InitializeAsync()
    {
        if (IsGhostscriptReady || IsInitializing || !IsPlatformSupported)
        {
            return;
        }

        IsInitializing = true;
        GhostscriptStatus = "正在初始化 Ghostscript…";
        GhostscriptSeverity = PdfStatusSeverity.Informational;
        NotifyStateChanged();
        try
        {
            await _ghostscriptService.InitializeAsync(CancellationToken.None);
            IsGhostscriptReady = true;
            GhostscriptStatus = $"Ghostscript {_ghostscriptService.VersionText ?? GhostscriptService.GhostscriptVersion} 已就绪";
            GhostscriptSeverity = PdfStatusSeverity.Success;
        }
        catch (Exception exception)
        {
            IsGhostscriptReady = false;
            GhostscriptStatus = $"Ghostscript 初始化失败：{exception.Message}";
            GhostscriptSeverity = PdfStatusSeverity.Error;
        }
        finally
        {
            IsInitializing = false;
            NotifyStateChanged();
        }
    }

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
        foreach (string candidate in filePaths)
        {
            if (!PdfOutlineFileRules.IsPdfFile(candidate))
            {
                continue;
            }

            string fullPath = Path.GetFullPath(candidate);
            if (!existingPaths.Add(fullPath))
            {
                continue;
            }

            Files.Add(new PdfFileItem(fullPath) { PoolId = _filePool?.Track(fullPath) });
            added++;
        }

        if (added > 0)
        {
            BatchStatus = $"已添加 {added} 个 PDF，共 {Files.Count} 个。";
            BatchSeverity = PdfStatusSeverity.Informational;
            IsBatchStatusOpen = true;
        }

        NotifyStateChanged();
        return added;
    }

    internal void RemoveFiles(IEnumerable<PdfFileItem> selectedFiles)
    {
        if (!CanEditFiles)
        {
            return;
        }

        foreach (PdfFileItem item in selectedFiles.Distinct().ToArray())
        {
            Files.Remove(item);
        }

        SelectedItemCount = 0;
        BatchStatus = Files.Count == 0 ? "就绪" : $"列表中剩余 {Files.Count} 个 PDF。";
        BatchSeverity = PdfStatusSeverity.Informational;
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
        BatchStatus = "就绪";
        BatchSeverity = PdfStatusSeverity.Informational;
        IsBatchStatusOpen = true;
        NotifyStateChanged();
    }

    internal void SetOutputDirectory(string? directory)
    {
        if (!CanEditFiles)
        {
            return;
        }

        OutputDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    internal async Task<PdfBatchSummary> StartConversionAsync()
    {
        if (!CanStart)
        {
            return default;
        }

        PdfFileItem[] files = [.. Files];
        string? outputDirectory = OutputDirectory;
        var mode = (PdfConvertMode)ConvertModeIndex;
        int dpi = SelectedDpi;
        if (!ReservePoolFiles(files.SelectMany(f => new[] {
            new FileAccessPath(f.FilePath), new FileAccessPath(PdfOutlineFileRules.GetOutputPath(f.FilePath, outputDirectory))
        }), out var poolLease)) return default;
        using var reservedFiles = poolLease;
        foreach (PdfFileItem file in files)
        {
            file.SetWaiting();
        }

        _conversionCancellation = new CancellationTokenSource();
        CancellationToken token = _conversionCancellation.Token;
        int maximumConcurrency = PdfOutlineFileRules.ClampThreadCount(SelectedThreadCount);
        using var semaphore = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);

        IsConverting = true;
        OverallProgress = 0;
        BatchStatus = $"正在转换 0/{files.Length}…";
        BatchSeverity = PdfStatusSeverity.Informational;
        IsBatchStatusOpen = true;
        NotifyStateChanged();

        int succeeded = 0;
        int failed = 0;
        int cancelled = 0;
        int processed = 0;

        async Task ProcessFileAsync(PdfFileItem file)
        {
            bool enteredSemaphore = false;
            try
            {
                await semaphore.WaitAsync(token);
                enteredSemaphore = true;
                token.ThrowIfCancellationRequested();

                file.SetConverting();
                string outputPath = PdfOutlineFileRules.GetOutputPath(
                    file.FilePath,
                    outputDirectory);
                await _ghostscriptService.ConvertAsync(
                    file.FilePath,
                    outputPath,
                    mode,
                    dpi,
                    token);
                file.SetCompleted();
                _filePool?.Service.ReplacePdfOutput(file.FilePath, outputPath);
                Interlocked.Increment(ref succeeded);
            }
            catch (OperationCanceledException)
            {
                file.SetCancelled();
                Interlocked.Increment(ref cancelled);
            }
            catch (Exception exception)
            {
                file.SetFailed(exception);
                Interlocked.Increment(ref failed);
            }
            finally
            {
                if (enteredSemaphore)
                {
                    semaphore.Release();
                }

                int done = Interlocked.Increment(ref processed);
                OverallProgress = done * 100d / files.Length;
                BatchStatus = token.IsCancellationRequested
                    ? $"正在取消，已处理 {done}/{files.Length}…"
                    : $"正在转换 {done}/{files.Length}…";
            }
        }

        try
        {
            await Task.WhenAll(files.Select(ProcessFileAsync));
        }
        finally
        {
            IsConverting = false;
            _conversionCancellation.Dispose();
            _conversionCancellation = null;
            NotifyStateChanged();
        }

        var summary = new PdfBatchSummary(files.Length, succeeded, failed, cancelled);
        BatchStatus = cancelled > 0
            ? $"转换已取消：成功 {succeeded}，失败 {failed}，取消 {cancelled}。"
            : $"转换完成：成功 {succeeded}，失败 {failed}。";
        BatchSeverity = failed > 0
            ? PdfStatusSeverity.Warning
            : cancelled > 0
                ? PdfStatusSeverity.Informational
                : PdfStatusSeverity.Success;
        IsBatchStatusOpen = true;
        return summary;
    }

    internal void Cancel()
    {
        if (!IsConverting)
        {
            return;
        }

        BatchStatus = "正在取消；已开始的 Ghostscript 任务完成后停止。";
        BatchSeverity = PdfStatusSeverity.Informational;
        IsBatchStatusOpen = true;
        _conversionCancellation?.Cancel();
    }

    partial void OnConvertModeIndexChanged(int value)
    {
        if (value is < 0 or > 1)
        {
            ConvertModeIndex = 0;
            return;
        }

        OnPropertyChanged(nameof(IsImageMode));
        OnPropertyChanged(nameof(CanConfigureImageMode));
    }

    partial void OnSelectedThreadCountChanged(int value)
    {
        int clamped = PdfOutlineFileRules.ClampThreadCount(value);
        if (value != clamped)
        {
            SelectedThreadCount = clamped;
        }
    }

    partial void OnSelectedDpiChanged(int value)
    {
        int clamped = PdfOutlineFileRules.ClampDpi(value);
        if (value != clamped)
        {
            SelectedDpi = clamped;
        }
    }

    partial void OnOutputDirectoryChanged(string? value) =>
        OnPropertyChanged(nameof(OutputDirectoryDisplay));

    partial void OnSelectedItemCountChanged(int value) =>
        OnPropertyChanged(nameof(CanRemoveFiles));

    partial void OnIsConvertingChanged(bool value) =>
        NotifyStateChanged();

    partial void OnIsGhostscriptReadyChanged(bool value) =>
        OnPropertyChanged(nameof(CanStart));

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanEditFiles));
        OnPropertyChanged(nameof(CanRemoveFiles));
        OnPropertyChanged(nameof(CanConfigure));
        OnPropertyChanged(nameof(CanConfigureImageMode));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
