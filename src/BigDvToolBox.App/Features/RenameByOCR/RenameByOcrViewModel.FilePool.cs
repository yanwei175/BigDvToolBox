using BigDvToolBox_App.Features.FilePool;

namespace BigDvToolBox_App.Features.RenameByOCR;

internal partial class RenameByOcrViewModel
{
    internal void ReportPoolMessage(string message) => Status = message;
    public string InputSourceDisplay => !string.IsNullOrEmpty(FolderPath) ? FolderPath
        : HasFiles ? "来自公共文件池（支持跨文件夹）" : "尚未加载文件";
    partial void OnFolderPathChanged(string value) => OnPropertyChanged(nameof(InputSourceDisplay));
    private FilePoolBinding? _filePool;
    internal void ConnectFilePool(FilePoolService? service)
    {
        _filePool?.Dispose();
        _filePool = service is null ? null : new(service, ApplyPoolPaths);
        if (_filePool is not null) foreach (string path in Files) _filePool.Track(path);
    }
    internal static bool AcceptsPoolFile(string path) => File.Exists(path) &&
        Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".png" or ".jpg" or ".jpeg";

    internal async Task<FilePoolImportResult> ImportPoolFilesAsync(IReadOnlyList<string> paths)
    {
        if (!CanConfigure) return new(0, 0, paths.Count);
        int added = 0, duplicates = 0, skipped = 0;
        BeginOperation("正在加入文件…");
        try
        {
            foreach (string path in paths)
            {
                if (!AcceptsPoolFile(path)) { skipped++; continue; }
                string fullPath = FilePoolService.Normalize(path);
                if (Files.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) { duplicates++; continue; }
                Files.Add(fullPath); _filePool?.Track(fullPath); added++;
            }
            if (added > 0)
            {
                FolderPath = string.Empty;
                Results.Clear();
                NotifyCollectionStateChanged();
                await LoadCurrentPreviewAsync();
            }
            var result = new FilePoolImportResult(added, duplicates, skipped);
            Status = result.Message;
            return result;
        }
        finally { EndOperation(); }
    }

    private void ApplyPoolPaths(IReadOnlyList<PathChange> changes)
    {

        if (!string.IsNullOrEmpty(FolderPath)) FolderPath = FilePoolService.MapPath(FolderPath, changes);
        bool changed = false;
        for (int i = 0; i < Files.Count; i++)
        {
            string path = FilePoolService.MapPath(Files[i], changes);
            if (path == Files[i]) continue;
            Files[i] = path; changed = true;
        }
        if (!changed) return;
        Results.Clear(); _currentDocument = null;
        OnPropertyChanged(nameof(CurrentPreviewBytes)); OnPropertyChanged(nameof(CurrentDocument));
        NotifyCollectionStateChanged();
        if (!IsBusy) _ = LoadCurrentPreviewAsync();
    }

    private bool ReservePoolFiles(IEnumerable<FileAccessPath> paths, out IDisposable? lease)
    {
        lease = null;
        if (_filePool is null || _filePool.TryAcquire(paths, out lease)) return true;
        Status = "文件正在其他模块中使用，请稍后重试。"; HasError = true;
        return false;
    }
}
