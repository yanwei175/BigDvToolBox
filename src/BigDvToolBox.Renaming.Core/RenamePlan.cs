namespace BigDvToolBox.Renaming.Core;

public sealed record RenameEntry(string Path, EntryKind Kind);
public sealed record RenameOperation(RenameEntry Source, string TargetPath, string Error = "")
{
    public bool IsUnchanged => string.Equals(Source.Path, TargetPath, StringComparison.Ordinal);
    public string TargetName => System.IO.Path.GetFileName(TargetPath);
}

public static class RenamePlanner
{
    public static IReadOnlyList<RenameOperation> Create(IReadOnlyList<RenameEntry> entries, IReadOnlyList<RenameRule> rules)
    {
        var operations = new List<RenameOperation>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            RenameEntry entry = entries[i] with { Path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entries[i].Path)) };
            string name = Path.GetFileName(entry.Path);
            string error = "";
            for (int step = 0; step < rules.Count; step++)
            {
                try { name = rules[step].Apply(name, entry.Kind, i); }
                catch (ArgumentException ex) { error = $"方法 {step + 1}：{ex.Message}"; break; }
            }
            error = error.Length > 0 ? error : ValidateName(name) ?? "";
            string target = error.Length == 0 && Path.GetDirectoryName(entry.Path) is { } directory
                ? Path.Combine(directory, name) : entry.Path;
            if (Path.GetDirectoryName(entry.Path) is null) error = "不能重命名磁盘根目录。";
            operations.Add(new(entry, target, error));
        }
        return Validate(operations);
    }

    /// <summary>同时检查批次和当前磁盘；执行前必须再次调用。</summary>
    public static IReadOnlyList<RenameOperation> Validate(IReadOnlyList<RenameOperation> operations)
    {
        var errors = operations.Select(o => o.Error).ToArray();
        var sources = new HashSet<string>(operations.Select(o => o.Source.Path), StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            void Error(string message) { if (errors[i].Length == 0) errors[i] = message; }
            if (!seenSources.Add(op.Source.Path)) Error("源路径重复。");
            if (!(op.Source.Kind == EntryKind.File ? File.Exists(op.Source.Path) : Directory.Exists(op.Source.Path))) Error("源项目已不存在或类型改变。");
            if (ValidateName(op.TargetName) is { } invalid) Error(invalid);
            if (!string.Equals(Path.GetDirectoryName(op.Source.Path), Path.GetDirectoryName(op.TargetPath), StringComparison.OrdinalIgnoreCase)) Error("目标必须位于原目录。");
            if (targets.TryGetValue(op.TargetPath, out int previous))
            {
                Error("多个项目生成了相同目标名称。");
                if (errors[previous].Length == 0) errors[previous] = "多个项目生成了相同目标名称。";
            }
            else targets.Add(op.TargetPath, i);
            if (!sources.Contains(op.TargetPath) && (File.Exists(op.TargetPath) || Directory.Exists(op.TargetPath))) Error("目标已存在，不会覆盖。");
            if (op.TargetPath.Length >= 32760) Error("目标路径过长。");
        }
        // 同批父子项目会随着父目录改名而改变路径，第一版要求拆分批次。
        var ordered = operations.Select((o, index) => (o.Source.Path, o.Source.Kind, Index: index))
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].Kind != EntryKind.Folder) continue;
            string prefix = ordered[i].Path + Path.DirectorySeparatorChar;
            // 搜索前缀所在范围，避免逐个比较整个批次。
            int low = i + 1, high = ordered.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (StringComparer.OrdinalIgnoreCase.Compare(ordered[middle].Path, prefix) < 0) low = middle + 1;
                else high = middle;
            }
            for (int j = low; j < ordered.Length && ordered[j].Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase); j++)
            {
                errors[ordered[i].Index] = errors[ordered[j].Index] = "同时包含父文件夹及其内部项目，请分批处理。";
            }
        }
        return operations.Select((o, i) => o with { Error = errors[i] }).ToArray();
    }

    public static string? ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return "名称不能为空或为相对目录。";
        if (name.Length > 255 || name.EndsWith(' ') || name.EndsWith('.')) return "名称过长或以空格、句点结尾。";
        if (name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c))) return "名称包含 Windows 不允许的字符。";
        string device = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(device[3]))) return "名称使用了 Windows 保留设备名。";
        return null;
    }
}
