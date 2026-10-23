using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;

namespace BigDvToolBox_App.Features.PdfLayOut;

public partial class PdfLayOutCellViewModel(int row, int column) : ObservableObject
{
    public int Row { get; } = row;
    public int Column { get; } = column;
    [ObservableProperty] public partial PdfLayOutCellContent? Content { get; internal set; }
    [ObservableProperty] public partial PdfLayOutPagePreview? Preview { get; internal set; }
    [ObservableProperty] public partial bool IsLoading { get; internal set; }
    [ObservableProperty] public partial string? Error { get; internal set; }
    public string PositionText => $"第 {Row + 1} 行第 {Column + 1} 列";
    public string Label => IsLoading ? $"{PositionText} · 加载中…" : Error is not null ? $"{PositionText} · {Error}"
        : Content is { } c ? $"{PositionText} · {c.Document.FileName} · 第 {c.SourcePageIndex + 1} 页" : $"{PositionText} · 拖入 PDF 或双击选择";
}

public sealed record PdfLayOutLayoutChange(PdfLayOutSettings Settings, Guid? PresetId,
    IReadOnlyList<PdfLayOutCellContent> RemovedContents);

public partial class PdfLayOutViewModel
{
    private double _columns = 2, _rows = 2;
    private readonly Dictionary<(int, int), CancellationTokenSource> _cellLoads = [];
    private readonly ConcurrentDictionary<(PdfLayOutDocument, int), PdfLayOutPagePreview> _freePreviewCache = new();
    private readonly SemaphoreSlim _freeRenderGate = new(2, 2);
    private CancellationTokenSource? _freePreviewCancellation;
    private long _freePreviewVersion;
    internal long ModeRevision { get; private set; }
    private bool _allowGridRemoval;
    private Func<Task>? _pendingLayoutAction;
    public ObservableCollection<PdfLayOutCellViewModel> FreeCells { get; } = [];
    public event EventHandler<PdfLayOutCellViewModel>? FreeCellChanged;
    public string[] Modes { get; } = ["重复拼版", "自由拼版"];
    [ObservableProperty] public partial int ModeIndex { get; set; }
    [ObservableProperty] public partial PdfLayOutCellViewModel? SelectedCell { get; private set; }
    [ObservableProperty] public partial PdfLayOutLayoutChange? PendingLayoutChange { get; private set; }
    public bool IsFreeMode => ModeIndex == (int)PdfLayOutMode.Free;
    public bool IsRepeatMode => !IsFreeMode;
    public bool CanEditCells => IsFreeMode && !IsBusy;
    public bool HasPendingCellLoads => _cellLoads.Count > 0;
    public int OccupiedCellCount => FreeCells.Count(c => c.Content is not null);
    public string ModeSummary => IsFreeMode ? $"自由拼版 · 已放置 {OccupiedCellCount}/{FreeCells.Count} · 1 张印张" : "重复拼版 · 毫米";
    public string ExportSuggestedName => IsFreeMode ? "自由拼版.pdf" : Path.GetFileNameWithoutExtension(FileName) + "_拼版.pdf";
    private IEnumerable<PdfLayOutCellContent> FreeContents => FreeCells.Where(c => c.Content is not null).Select(c => c.Content!);
    private PdfLayOutDocument? ActiveDocument => IsFreeMode ? SelectedCell?.Content?.Document : SourceDocument;
    private int ActivePageIndex => IsFreeMode ? SelectedCell?.Content?.SourcePageIndex ?? 0 : SelectedPageIndex;
    public double Columns { get => _columns; set => SetGridCount(value, true); }
    public double Rows { get => _rows; set => SetGridCount(value, false); }

    private void SetGridCount(double value, bool columns)
    {
        int count = PdfLayOutCalculator.NormalizeGridCount(value);
        var settings = CreateSettingsSnapshot() with { Columns = columns ? count : (int)_columns, Rows = columns ? (int)_rows : count };
        string property = columns ? nameof(Columns) : nameof(Rows);
        if (!_isApplyingPreset && RequestGridRemoval(settings, null)) { OnPropertyChanged(property); return; }
        if (columns) SetProperty(ref _columns, count, property);
        else SetProperty(ref _rows, count, property);
    }

    private bool RequestGridRemoval(PdfLayOutSettings settings, Guid? presetId, Func<Task>? action = null)
    {
        if (_allowGridRemoval) return false;
        var removed = FreeContents.Where(c => c.Row >= settings.Rows || c.Column >= settings.Columns).ToArray();
        if (removed.Length == 0) return false;
        _pendingLayoutAction = action;
        PendingLayoutChange = new(settings, presetId, removed);
        return true;
    }

    public async Task ResolveLayoutChangeAsync(PdfLayOutLayoutChange request, bool accepted)
    {
        if (!ReferenceEquals(request, PendingLayoutChange)) return;
        var action = _pendingLayoutAction;
        _pendingLayoutAction = null;
        PendingLayoutChange = null;
        if (!accepted) { OnPropertyChanged(nameof(Columns)); OnPropertyChanged(nameof(Rows)); return; }
        _allowGridRemoval = true;
        try
        {
            if (action is not null) await action();
            else if (request.PresetId is { } id)
            {
                var preset = Presets.FirstOrDefault(p => p.Id == id);
                if (preset is not null) TrySelectPreset(preset);
            }
            else { ApplyPresetSettings(request.Settings); NotifyPresetDirtyState(); }
        }
        finally { _allowGridRemoval = false; }
    }

    private void SynchronizeCells()
    {
        int rows = PdfLayOutCalculator.NormalizeGridCount(Rows), columns = PdfLayOutCalculator.NormalizeGridCount(Columns);
        foreach (var cell in FreeCells.Where(c => c.Row >= rows || c.Column >= columns).ToArray())
        {
            CancelCellLoad(cell.Row, cell.Column);
            FreeCells.Remove(cell);
        }
        var existing = FreeCells.Select(c => (c.Row, c.Column)).ToHashSet();
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
                if (!existing.Contains((row, column))) FreeCells.Add(new(row, column));
        if (SelectedCell is not null && !FreeCells.Contains(SelectedCell)) SelectedCell = null;
        var needed = FreeContents.Select(c => (c.Document, c.SourcePageIndex)).ToHashSet();
        foreach (var key in _freePreviewCache.Keys.Where(k => !needed.Contains(k)).ToArray()) _freePreviewCache.TryRemove(key, out _);
        OnPropertyChanged(nameof(FreeCells));
        OnPropertyChanged(nameof(ModeSummary));
        OnPropertyChanged(nameof(OccupiedCellCount));
        OnPropertyChanged(nameof(FontWarningMessage));
        OnPropertyChanged(nameof(HasFontWarning));
    }

    public void SelectCell(PdfLayOutCellViewModel cell)
    {
        if (!CanEditCells || !FreeCells.Contains(cell)) return;
        SelectedCell = cell;
        NotifySourceChanged();
        FreeCellChanged?.Invoke(this, cell);
    }

    public async Task LoadCellAsync(PdfLayOutCellViewModel cell, string path)
    {
        if (!CanEditCells || _documentService is null || !FreeCells.Contains(cell)) return;
        CancelCellLoad(cell.Row, cell.Column);
        var cancellation = new CancellationTokenSource();
        _cellLoads[(cell.Row, cell.Column)] = cancellation;
        ErrorMessage = null;
        cell.IsLoading = true;
        cell.Error = null;
        NotifyCellState(cell);
        try
        {
            if (!string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("每个容器只接受一个本地 PDF 文件。");
            PdfLayOutDocument document = await _documentService.LoadAsync(path, cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsFreeMode || !FreeCells.Contains(cell)) return;
            // 同一路径只有一份最新元数据；失效的旧页码保留并报错，不能悄悄改选其他页。
            foreach (var other in FreeCells.Where(c => c.Content is { } content &&
                string.Equals(content.DocumentId, document.FilePath, StringComparison.OrdinalIgnoreCase)))
                other.Content = other.Content! with { Document = document };
            cell.Content = new(cell.Row, cell.Column, document, 0);
            cell.Preview = null;
            SelectedCell = cell;
            LastExportPath = null;
            StatusMessage = $"已向{cell.PositionText}放入 {document.FileName}。";
            RefreshFreeContents();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && FreeCells.Contains(cell))
            {
                cell.Error = exception.Message;
                ReportInputError($"{cell.PositionText}加载失败：{exception.Message}");
            }
        }
        finally
        {
            if (_cellLoads.TryGetValue((cell.Row, cell.Column), out var current) && ReferenceEquals(current, cancellation))
            {
                _cellLoads.Remove((cell.Row, cell.Column));
                cell.IsLoading = false;
                NotifyCellState(cell);
            }
            cancellation.Dispose();
        }
    }

    public void ClearCell(PdfLayOutCellViewModel cell)
    {
        if (!CanEditCells || !FreeCells.Contains(cell)) return;
        CancelCellLoad(cell.Row, cell.Column);
        cell.Content = null;
        cell.Preview = null;
        cell.Error = null;
        RefreshFreeContents();
    }

    public void CopyToCell(PdfLayOutCellContent content, PdfLayOutCellViewModel target)
    {
        if (!CanEditCells || !FreeCells.Contains(target) || content.Row == target.Row && content.Column == target.Column) return;
        CancelCellLoad(target.Row, target.Column);
        target.Content = content with { Row = target.Row, Column = target.Column };
        target.Preview = null;
        target.Error = null;
        SelectedCell = target;
        RefreshFreeContents();
    }

    public void SelectCellPage(PdfLayOutCellViewModel cell, int pageIndex)
    {
        if (!CanEditCells || !FreeCells.Contains(cell) || cell.Content is not { } content ||
            pageIndex < 0 || pageIndex >= content.Document.PageCount) return;
        CancelCellLoad(cell.Row, cell.Column);
        cell.Content = content with { SourcePageIndex = pageIndex };
        cell.Preview = null;
        cell.Error = null;
        RefreshFreeContents();
    }

    private void RefreshFreeContents()
    {
        RebuildLayout();
        NotifySourceChanged();
        OnPropertyChanged(nameof(ModeSummary));
        OnPropertyChanged(nameof(OccupiedCellCount));
        QueuePreview();
        foreach (var cell in FreeCells) NotifyCellState(cell);
    }

    private void NotifyCellState(PdfLayOutCellViewModel cell)
    {
        OnPropertyChanged(nameof(HasPendingCellLoads));
        NotifyCommandState();
        NotifyNavigationState();
        FreeCellChanged?.Invoke(this, cell);
    }

    private void CancelCellLoad(int row, int column)
    {
        if (_cellLoads.Remove((row, column), out var cancellation)) cancellation.Cancel();
        var cell = FreeCells.FirstOrDefault(c => c.Row == row && c.Column == column);
        if (cell is not null) cell.IsLoading = false;
    }

    internal void StopFreeOperations()
    {
        foreach (var key in _cellLoads.Keys.ToArray()) CancelCellLoad(key.Item1, key.Item2);
        _freePreviewCancellation?.Cancel();
        foreach (var cell in FreeCells) NotifyCellState(cell);
    }

    private Task QueueFreePreview()
    {
        _freePreviewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _freePreviewCancellation = cancellation;
        NotifyNavigationState();
        CancelOperationCommand.NotifyCanExecuteChanged();
        long version = ++_freePreviewVersion;
        var needed = FreeContents.Where(c => c.SourcePageIndex >= 0 && c.SourcePageIndex < c.Document.PageCount)
            .Select(c => (c.Document, c.SourcePageIndex)).Distinct().ToArray();
        foreach (var key in _freePreviewCache.Keys.Except(needed).ToArray()) _freePreviewCache.TryRemove(key, out _);
        foreach (var cell in FreeCells)
        {
            cell.Preview = cell.Content is { } content && _freePreviewCache.TryGetValue((content.Document, content.SourcePageIndex), out var preview) ? preview : null;
            if (cell.Content is { } c && c.SourcePageIndex >= c.Document.PageCount) cell.Error = "源页码已失效，请重新选页";
        }
        return RenderAllAsync();

        async Task RenderAllAsync()
        {
            try { if (_previewService is not null) await Task.WhenAll(needed.Where(k => !_freePreviewCache.ContainsKey(k)).Select(RenderAsync)); }
            finally
            {
                if (ReferenceEquals(_freePreviewCancellation, cancellation)) _freePreviewCancellation = null;
                cancellation.Dispose();
                NotifyNavigationState();
                CancelOperationCommand.NotifyCanExecuteChanged();
            }
        }
        async Task RenderAsync((PdfLayOutDocument Document, int Page) key)
        {
            bool entered = false;
            try
            {
                await _freeRenderGate.WaitAsync(cancellation.Token);
                entered = true;
                PdfLayOutPagePreview preview = await _previewService!.RenderAsync(key.Document, key.Page, cancellation.Token);
                if (cancellation.IsCancellationRequested || version != _freePreviewVersion || !IsFreeMode) return;
                _freePreviewCache[key] = preview;
                foreach (var cell in FreeCells.Where(c => c.Content is { } content && ReferenceEquals(content.Document, key.Document) && content.SourcePageIndex == key.Page))
                { cell.Preview = preview; if (cell.Error?.StartsWith("预览失败", StringComparison.Ordinal) == true) cell.Error = null; FreeCellChanged?.Invoke(this, cell); }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (version == _freePreviewVersion && !cancellation.IsCancellationRequested)
                    foreach (var cell in FreeCells.Where(cell => cell.Content is { } content && ReferenceEquals(content.Document, key.Document) && content.SourcePageIndex == key.Page))
                    { cell.Error = $"预览失败：{exception.Message}"; FreeCellChanged?.Invoke(this, cell); }
            }
            finally { if (entered) _freeRenderGate.Release(); }
        }
    }

    private string BuildFontWarning()
    {
        var warnings = IsFreeMode ? FreeContents.SelectMany(c => c.Document.UnembeddedFonts
            .Where(f => f.PageNumbers.Contains(c.SourcePageIndex + 1))
            .Select(f => $"{c.Document.FileName} · {f.FontName}（第 {c.SourcePageIndex + 1} 页）")).Distinct().ToArray()
            : SourceDocument?.UnembeddedFonts.Select(f => $"{f.FontName}（第 {string.Join("、", f.PageNumbers)} 页）").ToArray() ?? [];
        return warnings.Length == 0 ? string.Empty : "以下字体未嵌入 PDF：" + string.Join("；", warnings) +
            "。可能出现替代字体或缺字，请核对预览。导出时可选择“转曲后保存”，但转曲不能恢复缺失的原字体。";
    }
}
