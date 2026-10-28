using System.Collections.ObjectModel;
using System.ComponentModel;
using BigDvToolBox.Renaming.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BigDvToolBox_App.Features.BatchRename;

public partial class RenameRowViewModel(string path, EntryKind kind) : ObservableObject
{
    internal Guid? PoolId { get; set; }
    public EntryKind Kind { get; } = kind;
    public long ImportOrder { get; init; }
    [ObservableProperty] public partial string CurrentPath { get; set; } = path;
    [ObservableProperty] public partial string OriginalName { get; set; } = Path.GetFileName(path);
    [ObservableProperty] public partial bool Included { get; set; } = true;
    [ObservableProperty] public partial string TargetName { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusDisplay))] public partial string Status { get; set; } = "待预览";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusDisplay))] public partial string Error { get; set; } = "";
    public string StatusDisplay => Error.Length == 0 ? Status : $"{Status}：{Error}";
    public string Name => Path.GetFileName(CurrentPath);
    public string DirectoryPath => Path.GetDirectoryName(CurrentPath) ?? "";
    partial void OnCurrentPathChanged(string value)
    { OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(DirectoryPath)); }
}

public partial class BatchRenameViewModel : ObservableObject
{
    public ObservableCollection<RenameMethodViewModel> Methods { get; } = [];
    public ObservableCollection<RenameRowViewModel> Files { get; } = [];
    public ObservableCollection<RenameRowViewModel> Folders { get; } = [];
    public ObservableCollection<RenamePreset> Presets { get; } = [];
    public ObservableCollection<RenameRowViewModel> CurrentRows => ActiveTabIndex == 0 ? Files : Folders;
    public bool IsFolderMode => ActiveTabIndex == 1;
    public bool CanEdit => !IsBusy;
    public bool CanExecute => !IsBusy && _preview is { Count: > 0 } && _preview.Any(o => !o.IsUnchanged) && _preview.All(o => o.Error.Length == 0);
    public bool CanRemoveMethod => CanEdit && SelectedMethod is not null;
    public bool CanMoveUp => CanRemoveMethod && Methods.IndexOf(SelectedMethod!) > 0;
    public bool CanMoveDown => CanRemoveMethod && Methods.IndexOf(SelectedMethod!) < Methods.Count - 1;
    public string PresetStatus => HasUnsavedChanges ? "当前方法列表有未保存的修改" : "方案列表已保存";

    [ObservableProperty] public partial RenameMethodViewModel? SelectedMethod { get; set; }
    [ObservableProperty] public partial int ActiveTabIndex { get; set; }
    [ObservableProperty] public partial int SortIndex { get; set; }
    [ObservableProperty] public partial bool IncludeSubdirectories { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool HasUnsavedChanges { get; private set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "添加方法，然后拖入文件或文件夹。左侧方法由两个标签页共享。";
    [ObservableProperty] public partial double Progress { get; private set; }
    [ObservableProperty] public partial string Summary { get; private set; } = "尚未添加项目";

    private IReadOnlyList<RenameOperation>? _preview;
    private RenameRowViewModel[] _previewRows = [];
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _operationCancellation;
    private int _revision;
    private bool _changing;
    private readonly string _presetDirectory;
    private long _nextImportOrder;
    private int _fileSortIndex;
    private int _folderSortIndex;

    public BatchRenameViewModel(string? presetDirectory = null)
    {
        _presetDirectory = presetDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BigDvToolBox", "RenamePresets");
        Methods.CollectionChanged += (_, _) =>
        {
            for (int i = 0; i < Methods.Count; i++) Methods[i].Order = i + 1;
            NotifyState();
        };
    }

    partial void OnSelectedMethodChanged(RenameMethodViewModel? oldValue, RenameMethodViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        NotifyState();
    }
    partial void OnHasUnsavedChangesChanged(bool value) => OnPropertyChanged(nameof(PresetStatus));
    partial void OnIsBusyChanged(bool value) => NotifyState();
    partial void OnActiveTabIndexChanged(int value)
    {
        SortIndex = IsFolderMode ? _folderSortIndex : _fileSortIndex;
        foreach (var method in Methods) method.IsFolderMode = IsFolderMode;
        OnPropertyChanged(nameof(CurrentRows)); OnPropertyChanged(nameof(IsFolderMode));
        Invalidate();
    }
    partial void OnSortIndexChanged(int value)
    {
        if (IsFolderMode) _folderSortIndex = value; else _fileSortIndex = value;
        SortRows(); Invalidate();
    }

    public void AddMethod(RenameMethod method)
    {
        if (!CanEdit) return;
        if (Methods.Count >= 500) { StatusText = "最多支持 500 个方法。"; return; }
        var item = new RenameMethodViewModel(new RenameRule { Method = method }) { IsExpanded = true, IsFolderMode = IsFolderMode };
        foreach (var existing in Methods) existing.IsExpanded = false;
        item.Changed += MethodChanged; Methods.Add(item); SelectedMethod = item;
        MethodChanged(this, EventArgs.Empty);
    }

    private void MethodChanged(object? sender, EventArgs e)
    { if (_changing) return; HasUnsavedChanges = true; Invalidate(); }

    public void CompleteMethodReorder(RenameMethodViewModel? draggedMethod, IReadOnlyList<Guid> originalOrder)
    {
        if (!CanEdit || Methods.Select(m => m.Id).SequenceEqual(originalOrder)) return;
        if (draggedMethod is not null && Methods.Contains(draggedMethod)) SelectedMethod = draggedMethod;
        MethodChanged(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveMethod))]
    private void RemoveMethod()
    {
        if (SelectedMethod is not { } item) return;
        int index = Methods.IndexOf(item);
        item.Changed -= MethodChanged; Methods.Remove(item);
        SelectedMethod = Methods.Count > 0 ? Methods[Math.Min(index, Methods.Count - 1)] : null;
        MethodChanged(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() { Methods.Move(Methods.IndexOf(SelectedMethod!), Methods.IndexOf(SelectedMethod!) - 1); MethodChanged(this, EventArgs.Empty); }
    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() { Methods.Move(Methods.IndexOf(SelectedMethod!), Methods.IndexOf(SelectedMethod!) + 1); MethodChanged(this, EventArgs.Empty); }

    public async Task AddPathsAsync(IEnumerable<string> paths, bool contents = false, bool recursive = false)
    {
        if (!CanEdit) return;
        IsBusy = true;
        bool folders = IsFolderMode;
        Progress = 0;
        StatusText = "正在加载项目，可取消当前操作。";
        _operationCancellation = new();
        try
        {
            string[] inputs = paths.ToArray();
            var token = _operationCancellation.Token;
            var found = await Task.Run(() =>
            {
                var entries = new List<string>();
                var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (string input in inputs)
                {
                    token.ThrowIfCancellationRequested();
                    if (Directory.Exists(input) && (contents || !folders))
                    {
                        foreach (string entry in folders ? Directory.EnumerateDirectories(input, "*", options) : Directory.EnumerateFiles(input, "*", options))
                        { token.ThrowIfCancellationRequested(); entries.Add(Path.GetFullPath(entry)); }
                    }
                    else if (folders ? Directory.Exists(input) : File.Exists(input)) entries.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(input)));
                }
                return entries;
            }, token);
            var rows = folders ? Folders : Files;
            var existing = new HashSet<string>(rows.Select(r => r.CurrentPath), StringComparer.OrdinalIgnoreCase);
            foreach (string path in found)
            {
                if (!existing.Add(path)) continue;
                var row = new RenameRowViewModel(path, folders ? EntryKind.Folder : EntryKind.File)
                { ImportOrder = _nextImportOrder++ };
                row.PoolId = folders ? null : _filePool?.Track(path);
                row.PropertyChanged += RowChanged; rows.Add(row);
            }
            SortRows(); StatusText = $"已加载 {rows.Count} 个{(folders ? "文件夹" : "文件")}，路径重复的项目已忽略。";
        }
        catch (OperationCanceledException) { StatusText = "已取消加载。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { StatusText = $"加载失败：{ex.Message}"; }
        finally { _operationCancellation.Dispose(); _operationCancellation = null; IsBusy = false; Invalidate(); }
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(RenameRowViewModel.Included) && !_changing) Invalidate(); }

    private void SortRows()
    {
        var sorted = SortIndex == 0 ? CurrentRows.OrderBy(r => r.ImportOrder).ToArray() : SortIndex == 1
            ? CurrentRows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.CurrentPath, StringComparer.OrdinalIgnoreCase).ToArray()
            : CurrentRows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.CurrentPath, StringComparer.OrdinalIgnoreCase).ToArray();
        CurrentRows.Clear(); foreach (var row in sorted) CurrentRows.Add(row);
    }

    public void RemoveRows(IEnumerable<RenameRowViewModel> rows)
    {
        if (!CanEdit) return;
        foreach (var row in rows.ToArray()) { row.PropertyChanged -= RowChanged; CurrentRows.Remove(row); }
        Invalidate();
    }

    public void SetIncluded(bool value)
    {
        if (!CanEdit) return;
        _changing = true; try { foreach (var row in CurrentRows) row.Included = value; } finally { _changing = false; }
        Invalidate();
    }

    public void Invalidate()
    {
        _revision++;
        _preview = null; _previewCancellation?.Cancel();
        foreach (var row in Files.Concat(Folders)) { row.TargetName = ""; row.Status = row.Included ? "待预览" : "已排除"; row.Error = ""; }
        Summary = $"共 {CurrentRows.Count} 项 · 已勾选 {CurrentRows.Count(r => r.Included)} 项";
        NotifyState();
        if (CanEdit) _ = DebouncedPreviewAsync();
    }

    private async Task DebouncedPreviewAsync()
    {
        var cancellation = new CancellationTokenSource(); _previewCancellation = cancellation;
        try { await Task.Delay(300, cancellation.Token); await GeneratePreviewAsync(cancellation.Token); }
        catch (OperationCanceledException) { }
        finally { if (_previewCancellation == cancellation) _previewCancellation = null; cancellation.Dispose(); }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    public async Task PreviewAsync() => await GeneratePreviewAsync(CancellationToken.None);

    private async Task GeneratePreviewAsync(CancellationToken token)
    {
        if (!CanEdit) return;
        int revision = _revision;
        var rows = CurrentRows.Where(r => r.Included).ToArray();
        try
        {
            if (!Methods.Any(m => m.Enabled)) { StatusText = "请添加并启用至少一个重命名方法。"; return; }
            var rules = Methods.Where(m => m.Enabled).Select(m => m.Snapshot()).ToArray();
            var entries = rows.Select(r => new RenameEntry(r.CurrentPath, r.Kind)).ToArray();
            var plan = await Task.Run(() => RenamePlanner.Create(entries, rules), token);
            if (revision != _revision || token.IsCancellationRequested || IsBusy) return;
            _preview = plan; _previewRows = rows;
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i].OriginalName = rows[i].Name;
                rows[i].TargetName = plan[i].TargetName; rows[i].Error = plan[i].Error;
                rows[i].Status = plan[i].Error.Length > 0 ? "错误" : plan[i].IsUnchanged ? "无需修改" : "待执行";
            }
            int errors = plan.Count(o => o.Error.Length > 0);
            Summary = $"共 {CurrentRows.Count} 项 · 将修改 {plan.Count(o => !o.IsUnchanged && o.Error.Length == 0)} 项 · 错误 {errors} 项";
            StatusText = errors == 0 ? "预览已更新，开始重命名只处理当前标签页中已勾选的项目。" : "请修正错误，或取消勾选错误项目后再执行。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { if (revision == _revision) { _preview = null; StatusText = ex.Message; } }
        finally { NotifyState(); }
    }

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        if (_preview is null) return;
        var plan = _preview; var rows = _previewRows;
        if (!ReservePoolFiles(plan.SelectMany(o => new[] {
            new BigDvToolBox_App.Features.FilePool.FileAccessPath(o.Source.Path, o.Source.Kind == EntryKind.Folder),
            new BigDvToolBox_App.Features.FilePool.FileAccessPath(o.TargetPath, o.Source.Kind == EntryKind.Folder)
        }), out var poolLease)) return;
        using var reservedFiles = poolLease;
        _previewCancellation?.Cancel(); _revision++; IsBusy = true; Progress = 0;
        StatusText = "正在执行重命名；取消时会尝试恢复原名称。";
        _operationCancellation = new();
        try
        {
            var progress = new Progress<int>(value => Progress = 100d * value / plan.Count);
            var result = await Task.Run(() => new RenameExecutor().Execute(plan, _operationCancellation.Token, progress));
            for (int i = 0; i < result.Items.Count; i++)
            {
                var item = result.Items[i]; rows[i].CurrentPath = item.CurrentPath; rows[i].Error = item.Error;
                rows[i].Status = item.Succeeded ? item.Operation.IsUnchanged ? "无需修改" : "已重命名"
                    : string.Equals(item.CurrentPath, item.Operation.Source.Path, StringComparison.Ordinal) ? "未执行／已恢复" : "需检查当前路径";
            }
            reservedFiles?.Dispose();
            _filePool?.PublishRenames(result.Items
                .Where(i => !string.Equals(i.Operation.Source.Path, i.CurrentPath, StringComparison.Ordinal))
                .Select(i => new BigDvToolBox_App.Features.FilePool.PathChange(i.Operation.Source.Path, i.CurrentPath, i.Operation.Source.Kind == EntryKind.Folder)).ToArray());
            int succeeded = result.Items.Count(i => i.Succeeded && !i.Operation.IsUnchanged);
            StatusText = $"{(result.Cancelled ? "操作已取消" : "批次结束")}：成功 {succeeded} 项，未完成 {result.Items.Count(i => !i.Succeeded)} 项。";
            Summary = StatusText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { StatusText = $"执行失败：{ex.Message}"; }
        finally { _preview = null; _operationCancellation.Dispose(); _operationCancellation = null; IsBusy = false; NotifyState(); }
    }

    public void Cancel() { _operationCancellation?.Cancel(); _previewCancellation?.Cancel(); }

    public void ApplyPreset(RenamePreset preset)
    {
        _changing = true;
        try
        {
            foreach (var rule in Methods) rule.Changed -= MethodChanged;
            Methods.Clear();
            foreach (var snapshot in preset.Rules)
            {
                var method = new RenameMethodViewModel(snapshot) { IsFolderMode = IsFolderMode };
                method.Changed += MethodChanged; Methods.Add(method);
            }
            SelectedMethod = Methods.FirstOrDefault();
            if (SelectedMethod is not null) SelectedMethod.IsExpanded = true;
            HasUnsavedChanges = false;
        }
        finally { _changing = false; }
        Invalidate();
    }

    public string SerializePreset(string name) => RenamePreset.Serialize(name, Methods.Select(m => m.Snapshot()).ToArray());
    public void MarkPresetUnsaved() => HasUnsavedChanges = true;

    public async Task LoadPresetsAsync()
    {
        try
        {
            if (!Directory.Exists(_presetDirectory)) return;
            foreach (string file in Directory.EnumerateFiles(_presetDirectory, "*.json"))
            {
                try { UpsertPreset(RenamePreset.Parse(await File.ReadAllTextAsync(file))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException) { StatusText = $"预设加载失败：{Path.GetFileName(file)}，{ex.Message}"; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText = $"读取预设目录失败：{ex.Message}"; }
    }

    public async Task<RenamePreset> SavePresetAsync(string name)
    {
        string json = SerializePreset(name);
        var preset = RenamePreset.Parse(json);
        IsBusy = true;
        try
        {
            Directory.CreateDirectory(_presetDirectory);
            string target = GetPresetPath(preset.Name);
            string temporary = target + ".tmp";
            await File.WriteAllTextAsync(temporary, json); File.Move(temporary, target, true);
            UpsertPreset(preset); HasUnsavedChanges = false; return preset;
        }
        finally { IsBusy = false; }
    }

    public async Task DeletePresetAsync(RenamePreset preset)
    {
        if (!CanEdit) return;
        IsBusy = true;
        try
        {
            await Task.Run(() => File.Delete(GetPresetPath(preset.Name)));
            var existing = Presets.FirstOrDefault(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) Presets.Remove(existing);
            HasUnsavedChanges = true;
        }
        finally { IsBusy = false; }
    }

    private string GetPresetPath(string name)
    {
        string id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name.ToUpperInvariant())));
        return Path.Combine(_presetDirectory, id + ".json");
    }

    public void UpsertPreset(RenamePreset preset)
    {
        var existing = Presets.FirstOrDefault(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is null) Presets.Add(preset); else Presets[Presets.IndexOf(existing)] = preset;
    }

    private void NotifyState()
    {
        foreach (string property in new[] { nameof(CanEdit), nameof(CanExecute), nameof(CanRemoveMethod), nameof(CanMoveUp), nameof(CanMoveDown) }) OnPropertyChanged(property);
        RemoveMethodCommand.NotifyCanExecuteChanged(); MoveUpCommand.NotifyCanExecuteChanged(); MoveDownCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged(); ExecuteCommand.NotifyCanExecuteChanged();
    }
}
