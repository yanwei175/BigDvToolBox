using System.Text.Json;
using System.Text.Json.Serialization;

namespace BigDvToolBox.Renaming.Core;

public sealed record RenamePreset(int Version, string Name, IReadOnlyList<RenameRule> Rules)
{
    public const int CurrentVersion = 1;
    public static string Serialize(string name, IReadOnlyList<RenameRule> rules)
    {
        var preset = new RenamePreset(CurrentVersion, name.Trim(), rules);
        Validate(preset);
        return JsonSerializer.Serialize(preset, RenamePresetJsonContext.Default.RenamePreset);
    }

    public static RenamePreset Parse(string json)
    {
        if (json.Length > 2_000_000) throw new ArgumentException("预设文件过大。");
        var preset = JsonSerializer.Deserialize(json, RenamePresetJsonContext.Default.RenamePreset) ?? throw new ArgumentException("预设文件为空。");
        Validate(preset);
        return preset;
    }

    private static void Validate(RenamePreset preset)
    {
        if (preset.Version != CurrentVersion) throw new ArgumentException("不支持此版本的预设。");
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 100) throw new ArgumentException("预设名称须为 1～100 个字符。");
        if (preset.Rules is null || preset.Rules.Count > 500) throw new ArgumentException("预设最多支持 500 个方法。");
        if (preset.Rules.Any(r => r is null || r.Validate() is not null)) throw new ArgumentException("预设包含无效方法或参数。");
        if (preset.Rules.Select(r => r.Id).Distinct().Count() != preset.Rules.Count) throw new ArgumentException("预设的方法标识重复。");
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RenamePreset))]
internal partial class RenamePresetJsonContext : JsonSerializerContext;
