using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BigDvToolBox_App.Features.FilePool;

public sealed partial class FilePoolEntry(string path) : ObservableObject
{
    public Guid Id { get; } = Guid.NewGuid();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Extension), nameof(Kind))]
    public partial string Path { get; internal set; } = path;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText))]
    public partial bool Exists { get; internal set; } = true;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    public string Kind => Extension == ".pdf" ? "PDF" : Extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif" or ".webp" ? "图片" : "其他";
    public string StateText => Exists ? Kind : "文件不存在";
}

internal sealed record FilePoolSnapshot(Guid Id, string Path);
internal sealed record PathChange(string Source, string Target, bool IsDirectory = false);
internal sealed record FileAccessPath(string Path, bool IsDirectory = false);
internal sealed record FilePoolImportResult(int Added, int Duplicates, int Skipped, bool Cancelled = false)
{
    public string Message => $"新增 {Added} 项，重复 {Duplicates} 项，跳过 {Skipped} 项。" + (Cancelled ? "导入已取消。" : "");
}

/// <summary>仅保存输入文件身份；不保存模块的勾选、预览和处理结果。</summary>
internal sealed class FilePoolService
{
    private readonly ObservableCollection<FilePoolEntry> _entries = [];
    private readonly Func<Action, Task> _onOwner;
    private readonly Dictionary<string, FilePoolEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private bool _batching;
    private void NotifyChanged() { if (!_batching) Changed?.Invoke(this, EventArgs.Empty); }
    private readonly List<(object Owner, Guid Token, FileAccessPath[] Paths)> _leases = [];
    private readonly object _leaseGate = new();
    public ReadOnlyObservableCollection<FilePoolEntry> Entries { get; }
    public event EventHandler? Changed;
    public event EventHandler<IReadOnlyList<PathChange>>? PathsChanged;

    public FilePoolService(Func<Action, Task>? onOwner = null)
    {
        _onOwner = onOwner ?? (action => { action(); return Task.CompletedTask; });
        Entries = new(_entries);
    }

    internal static string Normalize(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    internal static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static bool Within(string path, string directory) => Same(path, directory) ||
        path.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(directory) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static string MapPath(string path, IReadOnlyList<PathChange> changes)
    {
        // 同批交换名称必须按原始路径一次映射，不能逐个串行替换。
        var change = changes.OrderByDescending(c => c.Source.Length).FirstOrDefault(c =>
            Same(path, c.Source) || c.IsDirectory && Within(path, c.Source));
        return change is null ? path : change.Target + path[change.Source.Length..];
    }

    public FilePoolEntry? Find(string path) => _byPath.GetValueOrDefault(path);
    public FilePoolEntry? AddFile(string path)
    {
        try
        {
            path = Normalize(path);
            if (!File.Exists(path)) return null;
            var entry = Find(path);
            if (entry is not null) { entry.Exists = true; return entry; }
            entry = new(path); _byPath.Add(path, entry); _entries.Add(entry); NotifyChanged(); return entry;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    public async Task<FilePoolImportResult> ImportAsync(IEnumerable<string> inputs, bool recursive, CancellationToken token)
    {
        string[] paths = inputs.ToArray();
        int added = 0, duplicates = 0, skipped = 0;
        await Task.Run(async () =>
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(paths.Reverse());
            var batch = new List<string>(128);
            while (pending.TryPop(out string? input) && !token.IsCancellationRequested)
            {
                try
                {
                    string path = Normalize(input);
                    if (Directory.Exists(path))
                    {
                        if (!visited.Add(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                        try
                        {
                            foreach (string file in Directory.EnumerateFiles(path))
                            {
                                if (token.IsCancellationRequested) break;
                                await AddAsync(file);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                        if (recursive)
                        {
                            try { foreach (string child in Directory.EnumerateDirectories(path)) pending.Push(child); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                        }
                    }
                    else await AddAsync(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { skipped++; }
            }
            await FlushAsync();
            async Task AddAsync(string path)
            {
                batch.Add(path);
                if (batch.Count == 128) await FlushAsync();
            }
            async Task FlushAsync()
            {
                if (batch.Count == 0) return;
                string[] files = batch.ToArray(); batch.Clear();
                await _onOwner(() =>
                {
                    _batching = true;
                    try
                    {
                        foreach (string path in files)
                        {
                            if (!File.Exists(path)) { skipped++; continue; }
                            bool exists = Find(path) is not null;
                            if (AddFile(path) is null) skipped++;
                            else if (exists) duplicates++; else added++;
                        }
                    }
                    finally { _batching = false; NotifyChanged(); }
                });
            }
        });
        return new(added, duplicates, skipped, token.IsCancellationRequested);
    }

    public async Task RefreshAsync()
    {
        var snapshot = _entries.Select(e => (Entry: e, e.Path)).ToArray();
        var states = await Task.Run(() => snapshot.Select(e => File.Exists(e.Path)).ToArray());
        await _onOwner(() =>
        {
            for (int i = 0; i < snapshot.Length; i++)
                if (snapshot[i].Entry.Path == snapshot[i].Path) snapshot[i].Entry.Exists = states[i];
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        foreach (var entry in _entries.Where(e => selected.Contains(e.Id)).ToArray())
        { _entries.Remove(entry); _byPath.Remove(entry.Path); }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Clear() { _byPath.Clear(); _entries.Clear(); Changed?.Invoke(this, EventArgs.Empty); }
    public IReadOnlyList<FilePoolSnapshot> Snapshot(IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        return _entries.Where(e => selected.Contains(e.Id) && e.Exists && File.Exists(e.Path)).Select(e => new FilePoolSnapshot(e.Id, e.Path)).ToArray();
    }

    public void ApplyRenames(IReadOnlyList<PathChange> changes)
    {
        if (changes.Count == 0) return;
        foreach (var entry in _entries) { entry.Path = MapPath(entry.Path, changes); entry.Exists = File.Exists(entry.Path); }
        foreach (var group in _entries.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray())
            foreach (var duplicate in group.Skip(1).ToArray()) _entries.Remove(duplicate);
        _byPath.Clear(); foreach (var entry in _entries) _byPath.Add(entry.Path, entry);
        PathsChanged?.Invoke(this, changes);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ReplacePdfOutput(string source, string output)
    {
        if (!File.Exists(output)) return;
        if (!Same(source, output))
        {
            var entry = Find(source);
            if (entry is not null) { _entries.Remove(entry); _byPath.Remove(entry.Path); }
        }
        AddFile(output); Changed?.Invoke(this, EventArgs.Empty);
    }

    // 同一个模块可重入（预览/批次持有源路径，再预留输出路径），不同模块冲突即拒绝。
    public bool TryAcquire(object owner, IEnumerable<FileAccessPath> paths, out IDisposable? lease)
    {
        var requested = paths.Select(p => p with { Path = Normalize(p.Path) }).ToArray();
        lock (_leaseGate)
        {
            if (_leases.Any(active => !ReferenceEquals(active.Owner, owner) && active.Paths.Any(a => requested.Any(b =>
                Same(a.Path, b.Path) || a.IsDirectory && Within(b.Path, a.Path) || b.IsDirectory && Within(a.Path, b.Path)))))
            { lease = null; return false; }
            Guid id = Guid.NewGuid(); _leases.Add((owner, id, requested));
            lease = new ReleaseLease(() => { lock (_leaseGate) _leases.RemoveAll(l => l.Token == id); });
            return true;
        }
    }
    private sealed class ReleaseLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
