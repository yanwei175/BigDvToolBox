using System.ComponentModel;

namespace BigDvToolBox_App.Features.FilePool;

internal sealed record FilePoolNavigationContext(FilePoolService Service);
internal interface IFilePoolTarget
{
    FilePoolTarget? PoolTarget { get; }
}

internal sealed class FilePoolTarget(
    string name, INotifyPropertyChanged state, Func<bool> canAccept,
    Func<string, bool> accepts, Func<IReadOnlyList<string>, Task<FilePoolImportResult>> import,
    Action<string> report, Func<bool> isBusy, Action detach) : IDisposable
{
    private Action? _detach = detach;
    public string Name { get; } = name;
    public bool CanAccept => canAccept();
    public bool IsBusy => isBusy();
    public void Report(string message) => report(message);
    public bool Accepts(string path) => accepts(path);
    public event PropertyChangedEventHandler? StateChanged
    { add => state.PropertyChanged += value; remove => state.PropertyChanged -= value; }
    public Task<FilePoolImportResult> ImportAsync(IReadOnlyList<string> paths) => import(paths);
    public void Dispose() => Interlocked.Exchange(ref _detach, null)?.Invoke();
}

/// <summary>模块输入身份与公共文件池的连接。移出池不会删除此处的任务身份。</summary>
internal sealed class FilePoolBinding : IDisposable
{
    public FilePoolService Service { get; }
    private readonly Action<IReadOnlyList<PathChange>> _pathsChanged;
    private bool _publishing;
    private readonly Dictionary<string, Guid> _identities = new(StringComparer.OrdinalIgnoreCase);
    public FilePoolBinding(FilePoolService service, Action<IReadOnlyList<PathChange>> pathsChanged)
    { Service = service; _pathsChanged = pathsChanged; service.PathsChanged += OnPathsChanged; }
    public Guid? Track(string path)
    {
        var entry = Service.AddFile(path);
        if (entry is not null) _identities[path] = entry.Id;
        return entry?.Id;
    }
    public Guid? Identity(string path) => _identities.GetValueOrDefault(path) is var id && id != Guid.Empty ? id : null;
    private void OnPathsChanged(object? sender, IReadOnlyList<PathChange> changes)
    {
        var mapped = _identities.Select(p => (Path: FilePoolService.MapPath(p.Key, changes), p.Value)).ToArray();
        _identities.Clear(); foreach (var item in mapped) _identities[item.Path] = item.Value;
        if (!_publishing) _pathsChanged(changes);
    }
    public void PublishRenames(IReadOnlyList<PathChange> changes)
    {
        _publishing = true;
        try { Service.ApplyRenames(changes); }
        finally { _publishing = false; }
    }
    public bool TryAcquire(IEnumerable<FileAccessPath> paths, out IDisposable? lease) => Service.TryAcquire(this, paths, out lease);
    public void Dispose() => Service.PathsChanged -= OnPathsChanged;
}
