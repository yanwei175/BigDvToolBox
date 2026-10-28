using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BigDvToolBox_App.Features.FilePool;

internal sealed partial class FilePoolViewModel : ObservableObject, IDisposable
{
    private FilePoolService? _service;
    private FilePoolTarget? _target;
    private CancellationTokenSource? _scanCancellation;
    private readonly HashSet<Guid> _selected = [];
    public ObservableCollection<FilePoolEntry> VisibleFiles { get; } = [];
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial int TypeIndex { get; set; }
    [ObservableProperty] public partial bool OnlyCompatible { get; set; } = true;
    [ObservableProperty] public partial bool IncludeSubdirectories { get; set; }
    [ObservableProperty] public partial bool IsImporting { get; private set; }
    [ObservableProperty] public partial bool IsSending { get; private set; }
    [ObservableProperty] public partial string Message { get; set; } = "文件池仅在本次运行期间共享。移出不会删除磁盘文件。";
    public int Count => _service?.Entries.Count ?? 0;
    public string Summary => $"共 {Count} 项 · 当前显示 {VisibleFiles.Count} 项 · 已选 {_selected.Count} 项";
    public string ImportLabel => _target is null ? "加入当前功能" : $"加入{_target.Name}";
    public string TargetHint => _target is null ? "当前页面不支持接收文件。" : !_target.CanAccept
        ? "当前功能正在处理任务或暂不可用，请稍后再加入。" : "选择文件后加入当前功能，已有任务和设置会保留。";
    public bool CanSend => !IsImporting && !IsSending && _target?.CanAccept == true &&
        VisibleFiles.Any(e => _selected.Contains(e.Id) && e.Exists && _target.Accepts(e.Path));
    public bool CanEdit => !IsImporting && !IsSending;
    public bool CanRemove => CanEdit && _selected.Count > 0;
    public bool CanClear => CanEdit && Count > 0;
    public void Connect(FilePoolService service)
    {
        if (_service is not null) _service.Changed -= ServiceChanged;
        _service = service; service.Changed += ServiceChanged; Rebuild();
    }
    public void SetTarget(FilePoolTarget? target)
    {
        if (_target is not null) _target.StateChanged -= TargetChanged;
        _target = target;
        if (target is not null) target.StateChanged += TargetChanged;
        Rebuild();
    }
    private void ServiceChanged(object? sender, EventArgs e) => Rebuild();
    private void TargetChanged(object? sender, PropertyChangedEventArgs e) => NotifyState();
    public void Select(IEnumerable<Guid> ids) { _selected.Clear(); _selected.UnionWith(ids); NotifyState(); }
    public async Task RefreshAsync() { if (_service is not null) await _service.RefreshAsync(); }
    public async Task ImportAsync(IEnumerable<string> paths)
    {
        if (_service is null || !CanEdit) return;
        _scanCancellation = new(); IsImporting = true;
        Message = "正在收录文件，可取消；文件夹本身不会入池。";
        try { Message = (await _service.ImportAsync(paths, IncludeSubdirectories, _scanCancellation.Token)).Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Message = $"导入失败：{ex.Message}"; }
        finally { IsImporting = false; _scanCancellation.Dispose(); _scanCancellation = null; }
    }
    public void CancelImport() => _scanCancellation?.Cancel();
    public void RemoveSelected() { if (CanRemove) _service?.Remove(_selected.ToArray()); }
    public void Clear() { if (CanClear) _service?.Clear(); }
    public async Task<bool> SendAsync()
    {
        if (!CanSend || _service is null || _target is null) return false;
        var target = _target;
        var ids = _selected.ToArray();
        IsSending = true;
        try
        {
            await _service.RefreshAsync();
            if (target != _target || !target.CanAccept) { Message = "当前功能已变化或正在执行，请重试。"; return false; }
            var snapshot = _service.Snapshot(ids);
            if (snapshot.Count == 0) { Message = "选中的文件已失效，请重新选择。"; return false; }
            var result = await target.ImportAsync(snapshot.Select(e => e.Path).ToArray());
            result = result with { Skipped = result.Skipped + ids.Length - snapshot.Count };
            Message = result.Message; target.Report(Message);
            return result.Added > 0 || result.Duplicates > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { Message = $"加入失败：{ex.Message}"; return false; }
        finally { IsSending = false; }
    }
    private void Rebuild()
    {
        var desired = (_service?.Entries.AsEnumerable() ?? []).Where(e =>
            (SearchText.Length == 0 || e.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)) &&
            (TypeIndex == 0 || TypeIndex == 1 && e.Kind == "PDF" || TypeIndex == 2 && e.Kind == "图片" || TypeIndex == 3 && e.Kind == "其他") &&
            (!OnlyCompatible || _target is null || _target.Accepts(e.Path) || !e.Exists)).ToArray();
        var desiredSet = desired.ToHashSet();
        foreach (var entry in VisibleFiles.Where(e => !desiredSet.Contains(e)).ToArray()) VisibleFiles.Remove(entry);
        var existing = VisibleFiles.ToHashSet();
        for (int i = 0; i < desired.Length; i++) if (!existing.Contains(desired[i])) VisibleFiles.Insert(i, desired[i]);
        _selected.IntersectWith(VisibleFiles.Select(e => e.Id));
        OnPropertyChanged(nameof(Count)); NotifyState();
    }
    private void NotifyState()
    {
        foreach (string property in new[] { nameof(Summary), nameof(ImportLabel), nameof(TargetHint), nameof(CanSend), nameof(CanEdit), nameof(CanRemove), nameof(CanClear) }) OnPropertyChanged(property);
    }
    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnTypeIndexChanged(int value) => Rebuild();
    partial void OnOnlyCompatibleChanged(bool value) => Rebuild();
    partial void OnIsImportingChanged(bool value) => NotifyState();
    partial void OnIsSendingChanged(bool value) => NotifyState();
    public void Dispose()
    {
        CancelImport();
        if (_service is not null) _service.Changed -= ServiceChanged;
        if (_target is not null) _target.StateChanged -= TargetChanged;
    }
}
