using BigDvToolBox_App.Features.FilePool;

namespace BigDvToolBox_App.Features.BatchRename;

public partial class BatchRenameViewModel
{
    internal void ReportPoolMessage(string message) => StatusText = message;
    private FilePoolBinding? _filePool;
    internal void ConnectFilePool(FilePoolService? service)
    {
        _filePool?.Dispose();
        _filePool = service is null ? null : new(service, ApplyPoolPaths);
        if (_filePool is not null) foreach (var row in Files) row.PoolId = _filePool.Track(row.CurrentPath);
    }
    private void ApplyPoolPaths(IReadOnlyList<PathChange> changes)
    {
        bool changed = false;
        var updates = Files.Concat(Folders).Select(row => (Row: row, Path: FilePoolService.MapPath(row.CurrentPath, changes))).ToArray();
        foreach (var (row, path) in updates)
        {
            if (string.Equals(row.CurrentPath, path, StringComparison.Ordinal)) continue;
            row.CurrentPath = path; changed = true;
            row.OriginalName = row.Name;
        }
        if (changed && !IsBusy) Invalidate();
    }
    internal bool AcceptsPoolFile(string path) => File.Exists(path);
    internal async Task<FilePoolImportResult> ImportPoolFilesAsync(IReadOnlyList<string> paths)
    {
        if (!CanEdit) return new(0, 0, paths.Count);
        int duplicates = paths.Count(path => Files.Any(row => FilePoolService.Same(row.CurrentPath, path)));
        int before = Files.Count;
        ActiveTabIndex = 0;
        await AddPathsAsync(paths);
        var result = new FilePoolImportResult(Files.Count - before, duplicates, paths.Count - duplicates - (Files.Count - before));
        StatusText = result.Message;
        return result;
    }
    private bool ReservePoolFiles(IEnumerable<FileAccessPath> paths, out IDisposable? lease)
    {
        lease = null;
        if (_filePool is null || _filePool.TryAcquire(paths, out lease)) return true;
        StatusText = "文件正在其他模块中使用，请稍后重试。";
        return false;
    }
}
