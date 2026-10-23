using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.ComponentModel;

namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>
/// 正式页面状态：管理文档元数据、版式参数、预览选择及可取消的输入输出命令。
/// 服务通过构造函数注入；服务未接入时对应命令保持禁用。
/// </summary>
public partial class PdfLayOutViewModel : ObservableObject
{
    private const int CustomPaperSizeIndex = 4;
    private readonly IPdfLayOutDocumentService? _documentService;
    private readonly IPdfLayOutExportService? _exportService;
    private readonly IPdfLayOutPrintService? _printService;
    private readonly IPdfLayOutPreviewService? _previewService;
    private CancellationTokenSource? _previewCancellation;
    private long _previewRequestId;
    private bool _isReplacingDocument;
    private bool _isUpdatingPaperSize;

    public PdfLayOutViewModel() { SetActivePreset(Presets[0]); RebuildLayout(); }

    public PdfLayOutViewModel(
        IPdfLayOutDocumentService? documentService,
        IPdfLayOutExportService? exportService,
        IPdfLayOutPrintService? printService,
        IPdfLayOutPreviewService? previewService = null,
        IPdfLayOutPresetStore? presetStore = null) : this()
    {
        _documentService = documentService;
        _exportService = exportService;
        _printService = printService;
        _previewService = previewService ?? documentService as IPdfLayOutPreviewService;
        _presetStore = presetStore;
    }

    public string[] PaperSizes { get; } =
        ["SRA3 · 320 × 450", "A3 · 297 × 420", "A4 · 210 × 297", "SRA4 · 225 × 320", "自定义"];
    public string[] ScaleModes { get; } = ["原始尺寸", "适合单元格", "填满单元格", "自定义"];
    public bool HasDocument => ActiveDocument is not null;
    public bool HasFontWarning => !string.IsNullOrEmpty(FontWarningMessage);
    public string FontWarningMessage => BuildFontWarning();
    public bool IsBusy => Operation != PdfLayOutOperation.Idle || IsPresetBusy;
    public bool CanEditGrid => (IsFreeMode || !IsAutomaticGrid) && !IsBusy;
    public bool CanLoadDocument => _documentService is not null && !IsBusy && (IsRepeatMode || SelectedCell is not null);
    public bool CanExport => _exportService is not null && HasValidLayout && !IsBusy && !HasPendingCellLoads && PendingLayoutChange is null;
    public bool CanPrint => _printService is not null && HasValidLayout && !IsBusy && !HasPendingCellLoads && PendingLayoutChange is null;
    public bool CanSelectPage => IsRepeatMode && HasDocument && !IsBusy;
    public bool CanGoPreviousPage => CanSelectPage && SelectedPageIndex > 0;
    public bool CanGoNextPage => CanSelectPage && SelectedPageIndex < SourceDocument!.PageCount - 1;
    public bool CanRefreshPreview => _previewService is not null && !IsBusy && (IsFreeMode ? OccupiedCellCount > 0 : CanSelectPage && !IsRenderingPreview);
    public bool CanCancelOperation => Operation != PdfLayOutOperation.Idle || IsRenderingPreview || HasPendingCellLoads || _freePreviewCancellation is not null;
    public IReadOnlyList<PdfLayOutSourcePage> SourcePages => SourceDocument?.Pages ?? [];
    public string PageCountText => $"/ {SourceDocument?.PageCount ?? 0}";
    public Task PreviewTask { get; private set; } = Task.CompletedTask;
    public string PreviewMessage => IsFreeMode ? CurrentLayout?.ErrorMessage ?? string.Empty : PreviewErrorMessage ?? (IsRenderingPreview ? "正在渲染页面…"
        : !HasDocument ? "选择或拖入一个 PDF 文件"
        : !HasValidLayout ? ValidationMessage
        : PagePreview is null ? "当前页暂无预览，可点击“刷新预览”重试。" : string.Empty);
    public bool HasPreviewMessage => !string.IsNullOrWhiteSpace(PreviewMessage);
    public bool HasValidLayout => CurrentLayout?.IsValid == true && (IsFreeMode ? OccupiedCellCount > 0 : SourceDocument is not null);
    public PdfLayOutSourcePage? CurrentSourcePage => ActiveDocument is { } document
        ? document.Pages[Math.Clamp(ActivePageIndex, 0, document.PageCount - 1)] : null;
    public int PreviewColumnCount => CurrentLayout?.Columns ?? 0;
    public int PreviewRowCount => CurrentLayout?.Rows ?? 0;
    public int CurrentPageNumber => HasDocument ? SelectedPageIndex + 1 : 0;
    public double PreviewPageWidth => CurrentSourcePage?.Size.Width ?? 1;
    public double PreviewPageHeight => CurrentSourcePage?.Size.Height ?? 1;
    public string FileName => ActiveDocument?.FileName ?? (IsFreeMode ? SelectedCell?.PositionText ?? "请先选择右侧容器" : "选择 PDF 文件");
    public string FileDetails => ActiveDocument is not { } document ? (IsFreeMode ? "每个容器可放一个 PDF，并单独选择页码" : "尚未加载 PDF")
        : $"{document.PageCount} 页 · {PreviewPageWidth:0.##} × {PreviewPageHeight:0.##} mm" +
          (IsFreeMode ? $" · 当前第 {ActivePageIndex + 1} 页" : string.Empty);
    public string CurrentPageSizeText => CurrentSourcePage is null ? string.Empty
        : $"{PreviewPageWidth:0.##} × {PreviewPageHeight:0.##} mm";
    public string TrimmedPageSummary => IsFreeMode ? "裁切、缩放和位移共用；每个页面相对其容器居中，超出部分裁切。" : CurrentLayout?.IsValid == true
        ? $"裁切后页面为 {CurrentLayout.CroppedPageSize.Width:0.##} × {CurrentLayout.CroppedPageSize.Height:0.##} mm。"
        : ValidationMessage;
    public string ValidationMessage => CurrentLayout?.ErrorMessage ?? (IsFreeMode ? "请向容器放入 PDF。" : !HasDocument ? "请选择 PDF 文件。" : string.Empty);

    [ObservableProperty] public partial PdfLayOutDocument? SourceDocument { get; private set; }
    [ObservableProperty] public partial PdfLayOutSheetLayout? CurrentLayout { get; private set; }
    [ObservableProperty] public partial PdfLayOutPagePreview? PagePreview { get; private set; }
    [ObservableProperty] public partial bool IsRenderingPreview { get; private set; }
    [ObservableProperty] public partial string? PreviewErrorMessage { get; private set; }
    [ObservableProperty] public partial PdfLayOutOperation Operation { get; private set; }
    [ObservableProperty] public partial string StatusMessage { get; private set; } = "请选择或拖入一个 PDF 文件。";
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }
    [ObservableProperty] public partial string? LastExportPath { get; private set; }
    [ObservableProperty] public partial int SelectedPageIndex { get; set; }
    [ObservableProperty] public partial int SelectedSection { get; set; }
    [ObservableProperty] public partial int PaperSizeIndex { get; set; } = 2;
    [ObservableProperty] public partial int OrientationIndex { get; set; }
    [ObservableProperty] public partial double SheetWidth { get; set; } = 210;
    [ObservableProperty] public partial double SheetHeight { get; set; } = 297;
    [ObservableProperty] public partial double MarginTop { get; set; }
    [ObservableProperty] public partial double MarginRight { get; set; }
    [ObservableProperty] public partial double MarginBottom { get; set; }
    [ObservableProperty] public partial double MarginLeft { get; set; }
    [ObservableProperty] public partial double OffsetX { get; set; }
    [ObservableProperty] public partial double OffsetY { get; set; }
    [ObservableProperty] public partial double CropTop { get; set; }
    [ObservableProperty] public partial double CropRight { get; set; }
    [ObservableProperty] public partial double CropBottom { get; set; }
    [ObservableProperty] public partial double CropLeft { get; set; }
    [ObservableProperty] public partial bool IsAutomaticGrid { get; set; } = true;
    [ObservableProperty] public partial double HorizontalGap { get; set; } = 5;
    [ObservableProperty] public partial double VerticalGap { get; set; } = 5;
    [ObservableProperty] public partial int ScaleModeIndex { get; set; }
    [ObservableProperty] public partial double ScalePercent { get; set; } = 100;
    [ObservableProperty] public partial bool ShowCropMarks { get; set; } = true;
    [ObservableProperty] public partial bool ShowInnerMarks { get; set; } = true;
    [ObservableProperty] public partial double CropMarkLength { get; set; } = 2.8;
    [ObservableProperty] public partial double CropMarkOffset { get; set; } = 1.1;
    [ObservableProperty] public partial bool ShowCellBorders { get; set; }
    // 仅影响屏幕预览，不进入打印或导出的版式快照。
    [ObservableProperty] public partial bool ShowPageContent { get; set; } = true;
    // 只控制本次页面的文件导出，不写入拼版配置，也不改变预览和打印。
    [ObservableProperty] public partial bool OutlineFontsOnExport { get; set; }

    public PdfLayOutSettings CreateSettingsSnapshot() => new()
    {
        Mode = (PdfLayOutMode)ModeIndex,
        SheetSize = new(SheetWidth, SheetHeight),
        SheetMargins = new(MarginLeft, MarginTop, MarginRight, MarginBottom),
        SourceCrop = new(CropLeft, CropTop, CropRight, CropBottom),
        Offset = new(OffsetX, OffsetY),
        GridMode = IsAutomaticGrid ? PdfLayOutGridMode.Automatic : PdfLayOutGridMode.Manual,
        Columns = PdfLayOutCalculator.NormalizeGridCount(Columns),
        Rows = PdfLayOutCalculator.NormalizeGridCount(Rows),
        HorizontalGap = HorizontalGap,
        VerticalGap = VerticalGap,
        ScaleMode = (PdfLayOutScaleMode)ScaleModeIndex,
        ScalePercent = ScalePercent,
        Marks = new()
        {
            ShowCropMarks = ShowCropMarks, ShowInnerMarks = ShowInnerMarks,
            Length = CropMarkLength, Offset = CropMarkOffset,
            ShowCellBorders = ShowCellBorders,
        },
    };

    internal void SetSourceDocument(PdfLayOutDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        SourceDocument = document;
        LastExportPath = null;
    }

    private PdfLayOutJob CreateJob(bool outlineFonts = false)
    {
        if (IsFreeMode) return PdfLayOutJob.CreateFree(FreeContents, CreateSettingsSnapshot(), new() { OutlineFonts = outlineFonts });
        PdfLayOutDocument document = SourceDocument ?? throw new InvalidOperationException("请先加载 PDF。");
        return new(document, CreateSettingsSnapshot()) { ExportOptions = new() { OutlineFonts = outlineFonts } };
    }

    [RelayCommand(CanExecute = nameof(CanLoadDocument))]
    private Task LoadDocumentAsync(string? filePath, CancellationToken cancellationToken) =>
        IsFreeMode && SelectedCell is { } cell ? LoadCellAsync(cell, filePath ?? string.Empty) :
        RunOperationAsync(PdfLayOutOperation.Loading, async token =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            if (!string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择 PDF 文件。", nameof(filePath));
            PdfLayOutDocument document = await _documentService!.LoadAsync(filePath, token);
            token.ThrowIfCancellationRequested();
            SetSourceDocument(document);
            await PreviewTask;
            token.ThrowIfCancellationRequested();
            StatusMessage = $"已加载 {document.FileName}，共 {document.PageCount} 页。";
        }, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportPdfAsync(string? outputPath, CancellationToken cancellationToken) =>
        RunOperationAsync(PdfLayOutOperation.Exporting, async token =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            string target = Path.GetFullPath(outputPath);
            PdfLayOutJob job = CreateJob(OutlineFontsOnExport);
            if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("导出文件必须使用 .pdf 扩展名。", nameof(outputPath));
            if (job.Sources.Keys.Any(path => string.Equals(target, path, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("导出路径不能与源 PDF 相同。");
            await _exportService!.ExportAsync(job, target, token);
            LastExportPath = target;
            StatusMessage = job.ExportOptions.OutlineFonts ? $"转曲拼版 PDF 已导出至：{target}" : $"拼版 PDF 已导出至：{target}";
        }, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanPrint))]
    private async Task PrintAsync(CancellationToken cancellationToken)
    {
        if (!CanPrint) return;
        await RunOperationAsync(PdfLayOutOperation.Printing, async token =>
        {
            PdfLayOutPrintResult result = await _printService!.PrintAsync(CreateJob(), token);
            StatusMessage = result == PdfLayOutPrintResult.Submitted ? "已提交打印任务。" : "已取消打印。";
        }, cancellationToken);
        if (_printService is IPdfLayOutPrintDiagnosticsProvider { LastDiagnosticPath: { } path })
            StatusMessage += $"\n打印诊断日志：{path}";
    }

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void CancelOperation()
    {
        StopFreeOperations();
        LoadDocumentCommand.Cancel();
        ExportPdfCommand.Cancel();
        PrintCommand.Cancel();
        _previewCancellation?.Cancel();
        StatusMessage = IsBusy ? "正在取消操作…" : "已取消页面预览。";
    }

    [RelayCommand(CanExecute = nameof(CanGoPreviousPage))]
    private void PreviousPage() => SelectedPageIndex--;

    [RelayCommand(CanExecute = nameof(CanGoNextPage))]
    private void NextPage() => SelectedPageIndex++;

    [RelayCommand(CanExecute = nameof(CanRefreshPreview))]
    private Task RefreshPreviewAsync() { _freePreviewCache.Clear(); return QueuePreview(); }

    internal void ReportInputError(string message)
    {
        ErrorMessage = message;
        StatusMessage = message;
    }

    internal void ReportPreviewError(string message) => PreviewErrorMessage = message;

    internal void StopPreview()
    {
        StopFreeOperations();
        Interlocked.Increment(ref _previewRequestId);
        _previewCancellation?.Cancel();
        _previewCancellation = null;
        PagePreview = null;
        IsRenderingPreview = false;
    }

    private Task QueuePreview()
    {
        if (IsFreeMode) return PreviewTask = QueueFreePreview();
        long requestId = Interlocked.Increment(ref _previewRequestId);
        _previewCancellation?.Cancel();
        _previewCancellation = null;
        PagePreview = null;
        PreviewErrorMessage = null;
        if (_previewService is null || SourceDocument is not { } document)
        {
            IsRenderingPreview = false;
            return PreviewTask = Task.CompletedTask;
        }

        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        IsRenderingPreview = true;
        return PreviewTask = RenderPreviewAsync(document, SelectedPageIndex, requestId, cancellation);
    }

    private async Task RenderPreviewAsync(PdfLayOutDocument document, int pageIndex, long requestId, CancellationTokenSource cancellation)
    {
        try
        {
            PdfLayOutPagePreview preview = await _previewService!.RenderAsync(document, pageIndex, cancellation.Token);
            if (!cancellation.IsCancellationRequested && requestId == Volatile.Read(ref _previewRequestId))
                PagePreview = preview;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (requestId == Volatile.Read(ref _previewRequestId))
                PreviewErrorMessage = $"页面预览失败：{exception.Message}";
        }
        finally
        {
            if (requestId == Volatile.Read(ref _previewRequestId))
            {
                _previewCancellation = null;
                IsRenderingPreview = false;
            }
            cancellation.Dispose();
        }
    }

    private async Task RunOperationAsync(PdfLayOutOperation operation, Func<CancellationToken, Task> action, CancellationToken token)
    {
        bool canStart = operation switch
        {
            PdfLayOutOperation.Loading => CanLoadDocument,
            PdfLayOutOperation.Exporting => CanExport,
            PdfLayOutOperation.Printing => CanPrint,
            _ => false,
        };
        if (!canStart)
            return;
        Operation = operation;
        ErrorMessage = null;
        StatusMessage = operation switch
        {
            PdfLayOutOperation.Loading => "正在加载 PDF…",
            PdfLayOutOperation.Exporting => "正在导出拼版 PDF…",
            _ => "正在准备打印…",
        };
        try
        {
            await action(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            StatusMessage = "操作已取消。";
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusMessage = $"操作失败：{exception.Message}";
        }
        finally
        {
            Operation = PdfLayOutOperation.Idle;
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(ModeIndex))
        {
            if (ModeIndex is < 0 or > 1) { ModeIndex = 0; return; }
            ModeRevision++;
            StopFreeOperations();
            _previewCancellation?.Cancel();
            foreach (string property in new[] { nameof(IsFreeMode), nameof(IsRepeatMode), nameof(ModeSummary), nameof(CanEditGrid), nameof(CanEditCells) })
                OnPropertyChanged(property);
            NotifySourceChanged();
            if (!_isUpdatingPaperSize) { RebuildLayout(); QueuePreview(); }
            if (!_isApplyingPreset) NotifyPresetDirtyState();
        }
        else if (e.PropertyName == nameof(SelectedCell))
        {
            NotifySourceChanged();
            NotifyCommandState();
        }
        else if (e.PropertyName == nameof(PendingLayoutChange)) NotifyCommandState();
        else if (e.PropertyName == nameof(SourceDocument))
        {
            _isReplacingDocument = true;
            try { SelectedPageIndex = 0; }
            finally { _isReplacingDocument = false; }
            NotifySourceChanged();
            RebuildLayout();
            QueuePreview();
        }
        else if (e.PropertyName == nameof(SelectedPageIndex) && !_isReplacingDocument)
        {
            NotifySourceChanged();
            RebuildLayout();
            QueuePreview();
        }
        else if (e.PropertyName == nameof(CurrentLayout))
        {
            foreach (string property in new[] { nameof(PreviewColumnCount), nameof(PreviewRowCount), nameof(TrimmedPageSummary), nameof(ValidationMessage), nameof(HasValidLayout) })
                OnPropertyChanged(property);
            NotifyCommandState();
            NotifyPreviewMessage();
        }
        else if (e.PropertyName is nameof(Operation) or nameof(IsPresetBusy))
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanEditGrid));
            OnPropertyChanged(nameof(CanEditCells));
            NotifyCommandState();
            NotifyNavigationState();
            NotifyPresetState();
        }
        else if (e.PropertyName is nameof(PagePreview) or nameof(PreviewErrorMessage) or nameof(IsRenderingPreview))
        {
            NotifyPreviewMessage();
            if (e.PropertyName == nameof(IsRenderingPreview))
            {
                NotifyNavigationState();
                CancelOperationCommand.NotifyCanExecuteChanged();
            }
        }
        else if (e.PropertyName is nameof(SheetWidth) or nameof(SheetHeight) or nameof(MarginTop) or nameof(MarginRight)
                 or nameof(MarginBottom) or nameof(MarginLeft) or nameof(OffsetX) or nameof(OffsetY)
                 or nameof(CropTop) or nameof(CropRight)
                 or nameof(CropBottom) or nameof(CropLeft) or nameof(IsAutomaticGrid) or nameof(Columns) or nameof(Rows)
                 or nameof(HorizontalGap) or nameof(VerticalGap) or nameof(ScaleModeIndex) or nameof(ScalePercent)
                 or nameof(ShowCropMarks) or nameof(ShowInnerMarks) or nameof(CropMarkLength) or nameof(CropMarkOffset)
                 or nameof(ShowCellBorders))
        {
            if (!_isUpdatingPaperSize)
            {
                RebuildLayout();
                if (IsFreeMode && e.PropertyName is nameof(Columns) or nameof(Rows)) QueuePreview();
            }
            if (e.PropertyName == nameof(IsAutomaticGrid))
                OnPropertyChanged(nameof(CanEditGrid));
            OnPropertyChanged(nameof(CanEditCells));
            if (!_isApplyingPreset) NotifyPresetDirtyState();
        }
    }

    private void RebuildLayout()
    {
        SynchronizeCells();
        var settings = CreateSettingsSnapshot();
        var layout = IsFreeMode ? PdfLayOutCalculator.CalculateFree(settings, FreeContents)
            : CurrentSourcePage is { } page ? PdfLayOutCalculator.Calculate(page, settings) : PdfLayOutCalculator.CalculateGrid(settings);
        CurrentLayout = IsRepeatMode && SourceDocument is { } document ? layout with
        {
            Placements = layout.Placements.Select(p => p with { SourceDocumentId = document.FilePath }).ToArray(),
        } : layout;
    }

    private void NotifySourceChanged()
    {
        foreach (string property in new[] { nameof(HasDocument), nameof(CurrentSourcePage), nameof(CurrentPageNumber),
                     nameof(PreviewPageWidth), nameof(PreviewPageHeight), nameof(FileName), nameof(FileDetails), nameof(CurrentPageSizeText),
                     nameof(ValidationMessage), nameof(TrimmedPageSummary) })
            OnPropertyChanged(property);
        OnPropertyChanged(nameof(ExportSuggestedName));
        OnPropertyChanged(nameof(SourcePages));
        OnPropertyChanged(nameof(PageCountText));
        OnPropertyChanged(nameof(HasFontWarning));
        OnPropertyChanged(nameof(FontWarningMessage));
        NotifyNavigationState();
    }

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanLoadDocument));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanPrint));
        LoadDocumentCommand.NotifyCanExecuteChanged();
        ExportPdfCommand.NotifyCanExecuteChanged();
        PrintCommand.NotifyCanExecuteChanged();
        CancelOperationCommand.NotifyCanExecuteChanged();
    }

    private void NotifyNavigationState()
    {
        foreach (string property in new[] { nameof(CanSelectPage), nameof(CanGoPreviousPage), nameof(CanGoNextPage),
                     nameof(CanRefreshPreview), nameof(CanCancelOperation) })
            OnPropertyChanged(property);
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        RefreshPreviewCommand.NotifyCanExecuteChanged();
    }

    private void NotifyPreviewMessage()
    {
        OnPropertyChanged(nameof(PreviewMessage));
        OnPropertyChanged(nameof(HasPreviewMessage));
    }

    partial void OnSelectedPageIndexChanged(int value)
    {
        int index = Math.Clamp(value, 0, (SourceDocument?.PageCount ?? 1) - 1);
        if (index != value)
            SelectedPageIndex = index;
    }

    partial void OnPaperSizeIndexChanged(int value)
    {
        if (_isApplyingPreset) return;
        (double width, double height) = value switch
        {
            0 => (320, 450), 1 => (297, 420), 2 => (210, 297), 3 => (225, 320), _ => (SheetWidth, SheetHeight),
        };
        if (value is >= 0 and < CustomPaperSizeIndex)
            SetPaperDimensions(OrientationIndex == 0 ? width : height, OrientationIndex == 0 ? height : width);
    }

    partial void OnOrientationIndexChanged(int value)
    {
        if (_isApplyingPreset) return;
        double shortEdge = Math.Min(SheetWidth, SheetHeight);
        double longEdge = Math.Max(SheetWidth, SheetHeight);
        SetPaperDimensions(value == 0 ? shortEdge : longEdge, value == 0 ? longEdge : shortEdge);
    }

    partial void OnSheetWidthChanged(double value) => SelectCustomPaperSize();
    partial void OnSheetHeightChanged(double value) => SelectCustomPaperSize();
    private void SelectCustomPaperSize()
    {
        if (!_isUpdatingPaperSize) PaperSizeIndex = CustomPaperSizeIndex;
    }

    private void SetPaperDimensions(double width, double height)
    {
        _isUpdatingPaperSize = true;
        try { SheetWidth = width; SheetHeight = height; }
        finally { _isUpdatingPaperSize = false; }
        RebuildLayout();
    }
}
