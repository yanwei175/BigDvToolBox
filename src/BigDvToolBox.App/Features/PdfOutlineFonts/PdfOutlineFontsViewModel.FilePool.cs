using BigDvToolBox_App.Features.FilePool;

namespace BigDvToolBox_App.Features.PdfOutlineFonts;

internal sealed partial class PdfOutlineFontsViewModel
{
    internal void ReportPoolMessage(string message) => BatchStatus = message;
    private FilePoolBinding? _filePool;
    internal void ConnectFilePool(FilePoolService? service)
    {
        _filePool?.Dispose();
        _filePool = service is null ? null : new(service, ApplyPoolPaths);
        if (_filePool is not null) foreach (var row in Files) row.PoolId = _filePool.Track(row.FilePath);
    }
    private void ApplyPoolPaths(IReadOnlyList<PathChange> changes)
    {
        var updates = Files.Select(row => (Row: row, Path: FilePoolService.MapPath(row.FilePath, changes))).ToArray();
        foreach (var (row, path) in updates)
        {
            if (string.Equals(row.FilePath, path, StringComparison.Ordinal)) continue;
            row.FilePath = path;
            row.SetWaiting();
        }
        NotifyStateChanged();
    }
    internal bool AcceptsPoolFile(string path) => PdfOutlineFileRules.IsPdfFile(path);
    internal Task<FilePoolImportResult> ImportPoolFilesAsync(IReadOnlyList<string> paths)
    {
        if (!CanConfigure) return Task.FromResult(new FilePoolImportResult(0, 0, paths.Count));
        int duplicates = paths.Count(path => Files.Any(row => FilePoolService.Same(row.FilePath, path)));
        int before = Files.Count;
        AddFiles(paths);
        var result = new FilePoolImportResult(Files.Count - before, duplicates, paths.Count - duplicates - (Files.Count - before));
        BatchStatus = result.Message;
        return Task.FromResult(result);
    }
    private bool ReservePoolFiles(IEnumerable<FileAccessPath> paths, out IDisposable? lease)
    {
        lease = null;
        if (_filePool is null || _filePool.TryAcquire(paths, out lease)) return true;
        BatchStatus = "文件正在其他模块中使用，请稍后重试。";
        return false;
    }
}
