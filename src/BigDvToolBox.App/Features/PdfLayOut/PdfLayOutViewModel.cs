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

    public PdfLayOutViewModel() { SetActivePreset(Presets[0]); }

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
    public bool HasDocument => SourceDocument is not null;
    public bool IsBusy => Operation != PdfLayOutOperation.Idle || IsPresetBusy;
    public bool CanEditGrid => !IsAutomaticGrid && !IsBusy;
    public bool CanLoadDocument => _documentService is not null && !IsBusy;
    public bool CanExport => _exportService is not null && HasValidLayout && !IsBusy;
    public bool CanPrint => _printService is not null && HasValidLayout && !IsBusy;
    public bool CanSelectPage => HasDocument && !IsBusy;
    public bool CanGoPreviousPage => CanSelectPage && SelectedPageIndex > 0;
    public bool CanGoNextPage => CanSelectPage && SelectedPageIndex < SourceDocument!.PageCount - 1;
    public bool CanRefreshPreview => _previewService is not null && CanSelectPage && !IsRenderingPreview;
    public bool CanCancelOperation => Operation != PdfLayOutOperation.Idle || IsRenderingPreview;
    public IReadOnlyList<PdfLayOutSourcePage> SourcePages => SourceDocument?.Pages ?? [];
    public string PageCountText => $"/ {SourceDocument?.PageCount ?? 0}";
    public Task PreviewTask { get; private set; } = Task.CompletedTask;
    public string PreviewMessage => PreviewErrorMessage ?? (IsRenderingPreview ? "正在渲染页面…"
        : !HasDocument ? "选择或拖入一个 PDF 文件"
        : !HasValidLayout ? ValidationMessage
        : PagePreview is null ? "当前页暂无预览，可点击“刷新预览”重试。" : string.Empty);
    public bool HasPreviewMessage => !string.IsNullOrWhiteSpace(PreviewMessage);
    public bool HasValidLayout => CurrentLayout?.IsValid == true;
    public PdfLayOutSourcePage? CurrentSourcePage => SourceDocument is { } document
        ? document.Pages[Math.Clamp(SelectedPageIndex, 0, document.PageCount - 1)] : null;
    public int PreviewColumnCount => CurrentLayout?.Columns ?? 0;
    public int PreviewRowCount => CurrentLayout?.Rows ?? 0;
    public int CurrentPageNumber => HasDocument ? SelectedPageIndex + 1 : 0;
    public double PreviewPageWidth => CurrentSourcePage?.Size.Width ?? 1;
    public double PreviewPageHeight => CurrentSourcePage?.Size.Height ?? 1;
    public string FileName => SourceDocument?.FileName ?? "选择 PDF 文件";
    public string FileDetails => SourceDocument is null ? "尚未加载 PDF"
        : $"{SourceDocument.PageCount} 页 · {PreviewPageWidth:0.##} × {PreviewPageHeight:0.##} mm";
    public string CurrentPageSizeText => CurrentSourcePage is null ? string.Empty
        : $"{PreviewPageWidth:0.##} × {PreviewPageHeight:0.##} mm";
    public string TrimmedPageSummary => CurrentLayout?.IsValid == true
        ? $"裁切后页面为 {CurrentLayout.CroppedPageSize.Width:0.##} × {CurrentLayout.CroppedPageSize.Height:0.##} mm。"
        : ValidationMessage;
    public string ValidationMessage => !HasDocument ? "请选择 PDF 文件。" : CurrentLayout?.ErrorMessage ?? string.Empty;

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
    [ObservableProperty] public partial double Columns { get; set; } = 2;
    [ObservableProperty] public partial double Rows { get; set; } = 2;
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

    public PdfLayOutSettings CreateSettingsSnapshot() => new()
    {
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

    private PdfLayOutJob CreateJob()
    {
        PdfLayOutDocument document = SourceDocument ?? throw new InvalidOperationException("请先加载 PDF。");
        return new(document, CreateSettingsSnapshot());
    }

    [RelayCommand(CanExecute = nameof(CanLoadDocument))]
    private Task LoadDocumentAsync(string? filePath, CancellationToken cancellationToken) =>
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
            PdfLayOutJob job = CreateJob();
            if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("导出文件必须使用 .pdf 扩展名。", nameof(outputPath));
            if (string.Equals(target, job.Document.FilePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("导出路径不能与源 PDF 相同。");
            await _exportService!.ExportAsync(job, target, token);
            LastExportPath = target;
            StatusMessage = $"拼版 PDF 已导出至：{target}";
        }, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanPrint))]
    private Task PrintAsync(CancellationToken cancellationToken) =>
        RunOperationAsync(PdfLayOutOperation.Printing, async token =>
        {
            PdfLayOutPrintResult result = await _printService!.PrintAsync(CreateJob(), token);
            StatusMessage = result == PdfLayOutPrintResult.Submitted ? "已提交打印任务。" : "已取消打印。";
        }, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void CancelOperation()
    {
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
    private Task RefreshPreviewAsync() => QueuePreview();

    internal void ReportInputError(string message)
    {
        ErrorMessage = message;
        StatusMessage = message;
    }

    internal void ReportPreviewError(string message) => PreviewErrorMessage = message;

    internal void StopPreview()
    {
        Interlocked.Increment(ref _previewRequestId);
        _previewCancellation?.Cancel();
        _previewCancellation = null;
        PagePreview = null;
        IsRenderingPreview = false;
    }

    private Task QueuePreview()
    {
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
        if (e.PropertyName == nameof(SourceDocument))
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
                RebuildLayout();
            if (e.PropertyName == nameof(IsAutomaticGrid))
                OnPropertyChanged(nameof(CanEditGrid));
            if (!_isApplyingPreset) NotifyPresetDirtyState();
        }
    }

    private void RebuildLayout() => CurrentLayout = CurrentSourcePage is { } page
        ? PdfLayOutCalculator.Calculate(page, CreateSettingsSnapshot()) : null;

    private void NotifySourceChanged()
    {
        foreach (string property in new[] { nameof(HasDocument), nameof(CurrentSourcePage), nameof(CurrentPageNumber),
                     nameof(PreviewPageWidth), nameof(PreviewPageHeight), nameof(FileName), nameof(FileDetails), nameof(CurrentPageSizeText),
                     nameof(ValidationMessage), nameof(TrimmedPageSummary) })
            OnPropertyChanged(property);
        OnPropertyChanged(nameof(SourcePages));
        OnPropertyChanged(nameof(PageCountText));
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
    partial void OnColumnsChanged(double value)
    {
        double normalized = PdfLayOutCalculator.NormalizeGridCount(value);
        if (value != normalized) Columns = normalized;
    }
    partial void OnRowsChanged(double value)
    {
        double normalized = PdfLayOutCalculator.NormalizeGridCount(value);
        if (value != normalized) Rows = normalized;
    }

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
