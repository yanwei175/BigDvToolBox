using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BigDvToolBox_App.Features.RenameByOCR;

internal partial class RenameByOcrViewModel : ObservableObject, IDisposable
{
    private readonly IDocumentRenderer _renderer;
    private readonly Func<IOcrService>? _ocrServiceFactory;
    private readonly HashSet<RenamePreviewItem> _observedResults = [];
    private CancellationTokenSource? _operationCancellation;
    private RenderedDocument? _currentDocument;

    public RenameByOcrViewModel()
        : this(new WindowsDocumentRenderer())
    {
    }

    internal RenameByOcrViewModel(
        IDocumentRenderer renderer,
        Func<IOcrService>? ocrServiceFactory = null)
    {
        _renderer = renderer;
        _ocrServiceFactory = ocrServiceFactory;
        Results.CollectionChanged += Results_CollectionChanged;
        IsSupported = RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86;
    }

    public ObservableCollection<string> Files { get; } = [];
    public ObservableCollection<RenamePreviewItem> Results { get; } = [];
    public bool IsSupported { get; }
    public bool IsUnsupported => !IsSupported;
    public bool HasFiles => Files.Count > 0;
    public bool HasResults => Results.Count > 0;
    public bool CanStart => IsSupported && HasFiles && !IsBusy &&
        (ModeIndex == (int)RenameMode.AnchorText
            ? !string.IsNullOrWhiteSpace(AnchorText)
            : SelectedRegion.IsValid);
    public bool CanApply => IsSupported && HasResults && !IsBusy &&
        Results.Any(item => item.CanRename);
    public bool CanExport => IsSupported && HasFiles && !IsBusy;
    public bool CanConfigure => IsSupported && !IsBusy;
    public bool CanNavigate => HasFiles && !IsBusy;
    public bool CanCancel => IsBusy;
    public bool IsProgressIndeterminate => IsBusy && ProgressMaximum <= 0;
    public string CurrentFileDisplay => HasFiles
        ? $"{SelectedIndex + 1}/{Files.Count} · {Path.GetFileName(CurrentFilePath)}"
        : "尚未选择文件夹";
    public string CurrentFilePath => HasFiles && SelectedIndex >= 0 && SelectedIndex < Files.Count
        ? Files[SelectedIndex]
        : string.Empty;
    public string ProgressText => ProgressMaximum <= 0
        ? Status
        : $"{Status} ({ProgressValue:0}/{ProgressMaximum:0})";

    internal byte[]? CurrentPreviewBytes => _currentDocument?.PngBytes;
    internal RenderedDocument? CurrentDocument => _currentDocument;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    [NotifyPropertyChangedFor(nameof(CanConfigure))]
    [NotifyPropertyChangedFor(nameof(CanNavigate))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial bool HasError { get; private set; }

    [ObservableProperty]
    public partial string FolderPath { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentFileDisplay))]
    [NotifyPropertyChangedFor(nameof(CurrentFilePath))]
    public partial int SelectedIndex { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial int ModeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    public partial string AnchorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double RegionXPercent { get; set; }

    [ObservableProperty]
    public partial double RegionYPercent { get; set; }

    [ObservableProperty]
    public partial double RegionWidthPercent { get; set; }

    [ObservableProperty]
    public partial double RegionHeightPercent { get; set; }

    [ObservableProperty]
    public partial double PreviewPixelWidth { get; private set; }

    [ObservableProperty]
    public partial double PreviewPixelHeight { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    public partial double ProgressMaximum { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    public partial string Status { get; private set; } = "请选择包含 PDF 或图片的文件夹。";

    [ObservableProperty]
    public partial int PdfDpi { get; set; } = 400;

    public NormalizedOcrRegion SelectedRegion => new(
        RegionXPercent / 100d,
        RegionYPercent / 100d,
        RegionWidthPercent / 100d,
        RegionHeightPercent / 100d);

    public async Task LoadFolderAsync(string folderPath)
    {
        if (!CanConfigure)
        {
            return;
        }

        BeginOperation("正在加载文件夹…");
        try
        {
            IReadOnlyList<string> files = RenameFileService.EnumerateSupportedFiles(folderPath);
            Files.Clear();
            foreach (string file in files)
            {
                Files.Add(file);
            }

            FolderPath = folderPath;
            SelectedIndex = 0;
            Results.Clear();
            NotifyCollectionStateChanged();

            if (files.Count == 0)
            {
                _currentDocument = null;
                PreviewPixelWidth = 0;
                PreviewPixelHeight = 0;
                OnPropertyChanged(nameof(CurrentPreviewBytes));
                OnPropertyChanged(nameof(CurrentDocument));
                Status = "文件夹中没有 PDF、PNG、JPG 或 JPEG 文件。";
                HasError = true;
                return;
            }

            HasError = false;
            Status = $"已加载 {files.Count} 个文件。";
            await LoadCurrentPreviewAsync();
        }
        catch (Exception exception)
        {
            ReportFolderInputError($"文件夹加载失败：{exception.Message}");
        }
        finally
        {
            EndOperation();
        }
    }

    internal void ReportFolderInputError(string message)
    {
        HasError = true;
        Status = message;
    }

    public async Task<RenderedDocument?> LoadCurrentPreviewAsync()
    {
        if (!HasFiles)
        {
            return null;
        }

        string path = CurrentFilePath;
        int pdfDpi = PdfDpi;
        try
        {
            Status = $"正在加载 {Path.GetFileName(path)}…";
            HasError = false;
            RenderedDocument document =
                await _renderer.RenderAsync(path, pdfDpi, CancellationToken.None);

            if (!path.Equals(CurrentFilePath, StringComparison.OrdinalIgnoreCase) ||
                pdfDpi != PdfDpi)
            {
                return null;
            }

            _currentDocument = document;
            PreviewPixelWidth = _currentDocument.PixelWidth;
            PreviewPixelHeight = _currentDocument.PixelHeight;
            Status = CurrentFileDisplay;
            OnPropertyChanged(nameof(CurrentPreviewBytes));
            OnPropertyChanged(nameof(CurrentDocument));
            return _currentDocument;
        }
        catch (Exception exception)
        {
            _currentDocument = null;
            HasError = true;
            Status = $"预览加载失败：{exception.Message}";
            OnPropertyChanged(nameof(CurrentPreviewBytes));
            OnPropertyChanged(nameof(CurrentDocument));
            return null;
        }
    }

    public async Task GenerateRenamePreviewAsync()
    {
        if (!CanStart)
        {
            return;
        }

        CancellationToken token = BeginOperation("正在识别文件…");
        var recognized = new List<(string Path, string Text, string Error)>(Files.Count);
        try
        {
            using IOcrService ocr = await Task.Run(CreateOcrService, token);
            int modeIndex = ModeIndex;
            int pdfDpi = PdfDpi;
            string anchorText = AnchorText;
            NormalizedOcrRegion selectedRegion = SelectedRegion;
            ProgressMaximum = Files.Count;
            for (int index = 0; index < Files.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                string path = Files[index];
                ProgressValue = index + 1;
                Status = $"正在识别 {Path.GetFileName(path)}";
                try
                {
                    RenderedDocument document =
                        await GetOrRenderDocumentAsync(path, pdfDpi, token);
                    byte[] input = modeIndex == (int)RenameMode.FixedRegion
                        ? await _renderer.CropAsync(document, selectedRegion, token)
                        : document.PngBytes;
                    OcrSegmentationMode segmentationMode =
                        modeIndex == (int)RenameMode.FixedRegion
                            ? OcrSegmentationMode.SingleWord
                            : OcrSegmentationMode.Auto;
                    string fullText =
                        await ocr.RecognizeAsync(input, token, segmentationMode);
                    if (string.IsNullOrWhiteSpace(fullText))
                    {
                        throw new InvalidDataException("OCR 未返回文本。");
                    }

                    string renameText = modeIndex == (int)RenameMode.AnchorText
                        ? OcrTextParser.ExtractAfterAnchor(fullText, anchorText)
                        : fullText;
                    recognized.Add((path, renameText, string.Empty));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    recognized.Add((path, string.Empty, exception.Message));
                }
            }

            Results.Clear();
            foreach (RenamePreviewItem result in RenamePlanner.Plan(recognized))
            {
                Results.Add(result);
            }

            NotifyCollectionStateChanged();
            int failures = Results.Count(item => !string.IsNullOrEmpty(item.Error));
            Status = failures == 0
                ? "识别完成，请检查目标文件名后确认重命名。"
                : $"识别完成，{failures} 个文件无法生成目标名称。";
            HasError = failures > 0;
        }
        catch (OperationCanceledException)
        {
            Status = "操作已取消。";
            HasError = false;
        }
        catch (Exception exception)
        {
            Status = $"OCR 处理失败：{exception.Message}";
            HasError = true;
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task<BatchSummary> ApplyRenamesAsync()
    {
        if (!CanApply)
        {
            return new BatchSummary(0, Results.Count, 0);
        }

        CancellationToken token = BeginOperation("正在重命名文件…");
        try
        {
            BatchSummary summary = await RenameFileService.ApplyAsync(Results, token);
            string summaryMessage =
                $"重命名完成：成功 {summary.Succeeded}，跳过 {summary.Skipped}，失败 {summary.Failed}。";
            HasError = summary.Failed > 0;
            await ReloadFolderAfterRenameAsync();
            Status = summaryMessage;
            return summary;
        }
        catch (OperationCanceledException)
        {
            Status = "重命名已取消。";
            return new BatchSummary(0, 0, 0);
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task ExportCsvAsync(string destinationPath)
    {
        if (!IsSupported || !HasFiles || IsBusy)
        {
            return;
        }

        CancellationToken token = BeginOperation("正在生成 OCR CSV…");
        try
        {
            using IOcrService ocr = await Task.Run(CreateOcrService, token);
            int pdfDpi = PdfDpi;
            var rows = new List<(string FileName, string Text)>();
            ProgressMaximum = Files.Count;
            for (int index = 0; index < Files.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                string path = Files[index];
                ProgressValue = index + 1;
                Status = $"正在导出 {Path.GetFileName(path)}";
                try
                {
                    RenderedDocument document = await _renderer.RenderAsync(path, pdfDpi, token);
                    string text = await ocr.RecognizeAsync(document.PngBytes, token);
                    rows.Add((Path.GetFileName(path), text));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    rows.Add((Path.GetFileName(path), $"[识别失败] {exception.Message}"));
                }
            }

            await RenameFileService.WriteCsvAsync(destinationPath, rows, token);
            Status = $"CSV 已保存到 {destinationPath}";
            HasError = false;
        }
        catch (OperationCanceledException)
        {
            Status = "CSV 导出已取消。";
        }
        catch (Exception exception)
        {
            Status = $"CSV 导出失败：{exception.Message}";
            HasError = true;
        }
        finally
        {
            EndOperation();
        }
    }

    public void SetSelectedRegion(NormalizedOcrRegion region)
    {
        RegionXPercent = Math.Round(region.X * 100, 2);
        RegionYPercent = Math.Round(region.Y * 100, 2);
        RegionWidthPercent = Math.Round(region.Width * 100, 2);
        RegionHeightPercent = Math.Round(region.Height * 100, 2);
        OnPropertyChanged(nameof(CanStart));
    }

    public async Task SelectPreviousAsync()
    {
        if (SelectedIndex <= 0 || IsBusy)
        {
            return;
        }

        SelectedIndex--;
        await LoadCurrentPreviewAsync();
    }

    public async Task SelectNextAsync()
    {
        if (SelectedIndex >= Files.Count - 1 || IsBusy)
        {
            return;
        }

        SelectedIndex++;
        await LoadCurrentPreviewAsync();
    }

    public void Cancel() => _operationCancellation?.Cancel();

    public void Dispose()
    {
        Cancel();
        _operationCancellation?.Dispose();
        Results.CollectionChanged -= Results_CollectionChanged;
        foreach (RenamePreviewItem item in _observedResults)
        {
            item.PropertyChanged -= Result_PropertyChanged;
        }
        _observedResults.Clear();
    }

    private void Results_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (RenamePreviewItem item in _observedResults)
            {
                item.PropertyChanged -= Result_PropertyChanged;
            }
            _observedResults.Clear();
        }

        if (e.OldItems is not null)
        {
            foreach (RenamePreviewItem item in e.OldItems)
            {
                item.PropertyChanged -= Result_PropertyChanged;
                _observedResults.Remove(item);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (RenamePreviewItem item in e.NewItems)
            {
                if (_observedResults.Add(item))
                {
                    item.PropertyChanged += Result_PropertyChanged;
                }
            }
        }

        OnPropertyChanged(nameof(CanApply));
    }

    private void Result_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RenamePreviewItem.CanRename))
        {
            OnPropertyChanged(nameof(CanApply));
        }
    }

    partial void OnModeIndexChanged(int value) => OnPropertyChanged(nameof(CanStart));
    partial void OnRegionXPercentChanged(double value) => OnPropertyChanged(nameof(CanStart));
    partial void OnRegionYPercentChanged(double value) => OnPropertyChanged(nameof(CanStart));
    partial void OnRegionWidthPercentChanged(double value) => OnPropertyChanged(nameof(CanStart));
    partial void OnRegionHeightPercentChanged(double value) => OnPropertyChanged(nameof(CanStart));

    private CancellationToken BeginOperation(string status)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        HasError = false;
        ProgressValue = 0;
        ProgressMaximum = 0;
        Status = status;
        return _operationCancellation.Token;
    }

    private void EndOperation()
    {
        IsBusy = false;
        ProgressValue = 0;
        ProgressMaximum = 0;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanApply));
    }

    private IOcrService CreateOcrService()
    {
        if (_ocrServiceFactory is not null)
        {
            return _ocrServiceFactory();
        }

        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("Rename By OCR 仅支持 x86 和 x64。");
        }

        string tessdataPath = Path.Combine(
            AppContext.BaseDirectory,
            "Features",
            "RenameByOCR",
            "tessdata");
        return new TesseractOcrService(tessdataPath);
    }

    private Task<RenderedDocument> GetOrRenderDocumentAsync(
        string path,
        int pdfDpi,
        CancellationToken cancellationToken)
    {
        if (_currentDocument is not null &&
            _currentDocument.SourcePath.Equals(path, StringComparison.OrdinalIgnoreCase) &&
            _currentDocument.PdfDpi == pdfDpi)
        {
            return Task.FromResult(_currentDocument);
        }

        return _renderer.RenderAsync(path, pdfDpi, cancellationToken);
    }

    private async Task ReloadFolderAfterRenameAsync()
    {
        IReadOnlyList<string> files = RenameFileService.EnumerateSupportedFiles(FolderPath);
        Files.Clear();
        foreach (string file in files)
        {
            Files.Add(file);
        }

        SelectedIndex = Math.Clamp(SelectedIndex, 0, Math.Max(0, Files.Count - 1));
        NotifyCollectionStateChanged();
        await LoadCurrentPreviewAsync();
    }

    private void NotifyCollectionStateChanged()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanConfigure));
        OnPropertyChanged(nameof(CanNavigate));
        OnPropertyChanged(nameof(CurrentFileDisplay));
        OnPropertyChanged(nameof(CurrentFilePath));
    }
}
