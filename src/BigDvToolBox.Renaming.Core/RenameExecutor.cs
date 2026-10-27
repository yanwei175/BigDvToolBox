namespace BigDvToolBox.Renaming.Core;

public interface IRenameFileSystem
{
    void Move(string source, string target, EntryKind kind);
}

public sealed record RenameExecutionItem(RenameOperation Operation, string CurrentPath, bool Succeeded, string Error);
public sealed record RenameExecutionResult(IReadOnlyList<RenameExecutionItem> Items, bool Cancelled);

public sealed class RenameExecutor(IRenameFileSystem? fileSystem = null)
{
    private readonly IRenameFileSystem _fileSystem = fileSystem ?? new DiskFileSystem();

    /// <summary>两阶段移动，支持名称交换。失败时按实际移动日志恢复，不覆盖任何目标。</summary>
    public RenameExecutionResult Execute(IReadOnlyList<RenameOperation> plan, CancellationToken cancellationToken = default,
        IProgress<int>? progress = null)
    {
        var validated = RenamePlanner.Validate(plan);
        if (validated.Any(o => o.Error.Length > 0))
            return new(validated.Select(o => new RenameExecutionItem(o, o.Source.Path, false,
                o.Error.Length > 0 ? o.Error : "批次校验未通过，未执行。")).ToArray(), false);
        var current = plan.Select(o => o.Source.Path).ToArray();
        var journal = new List<(int Index, string From, string To)>();
        string failure = "";
        bool cancelled = false;
        var recoveryErrors = new Dictionary<int, string>();
        try
        {
            var temporary = new string[plan.Count];
            for (int i = 0; i < plan.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (plan[i].IsUnchanged) continue;
                temporary[i] = Path.Combine(Path.GetDirectoryName(current[i])!, ".bdv-" + Guid.NewGuid().ToString("N"));
                Move(i, temporary[i]);
            }
            for (int i = 0; i < plan.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!plan[i].IsUnchanged) Move(i, plan[i].TargetPath);
                progress?.Report(i + 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
        {
            cancelled = ex is OperationCanceledException;
            failure = cancelled ? "操作已取消，已尝试恢复原名称。" : $"执行失败，已尝试恢复原名称：{ex.Message}";
            for (int j = journal.Count - 1; j >= 0; j--)
            {
                var move = journal[j];
                if (!string.Equals(current[move.Index], move.To, StringComparison.Ordinal)) continue;
                try
                {
                    _fileSystem.Move(move.To, move.From, plan[move.Index].Source.Kind);
                    current[move.Index] = move.From;
                }
                catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException or ArgumentException)
                { recoveryErrors[move.Index] = recovery.Message; }
            }
        }
        return new(plan.Select((o, i) => new RenameExecutionItem(o, current[i], failure.Length == 0,
            recoveryErrors.TryGetValue(i, out string? recovery)
                ? $"{failure} 恢复失败：{recovery}；当前路径：{current[i]}" : failure)).ToArray(), cancelled);

        void Move(int index, string target)
        {
            string source = current[index];
            _fileSystem.Move(source, target, plan[index].Source.Kind);
            current[index] = target;
            journal.Add((index, source, target));
        }
    }

    private sealed class DiskFileSystem : IRenameFileSystem
    {
        public void Move(string source, string target, EntryKind kind)
        {
            if (kind == EntryKind.Folder) Directory.Move(source, target);
            else File.Move(source, target, overwrite: false);
        }
    }
}
