using BigDvToolBox.Renaming.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BigDvToolBox_App.Features.BatchRename;

public partial class RenameMethodViewModel : ObservableObject
{
    public static IReadOnlyList<string> MethodNames { get; } = ["替换文字", "添加文字", "按位置删除", "删除指定字符", "大小写转换", "添加序号"];
    public RenameMethod Method { get; }
    public Guid Id { get; }
    public string Title => $"{Order:00}  {MethodNames[(int)Method]}";
    public event EventHandler? Changed;

    [ObservableProperty] public partial int Order { get; set; }
    [ObservableProperty] public partial bool Enabled { get; set; } = true;
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial string Replacement { get; set; } = "";
    [ObservableProperty] public partial bool MatchCase { get; set; }
    [ObservableProperty] public partial int OccurrenceMode { get; set; }
    [ObservableProperty] public partial double OccurrenceNumber { get; set; } = 1;
    [ObservableProperty] public partial double Position { get; set; }
    [ObservableProperty] public partial bool FromRight { get; set; }
    [ObservableProperty] public partial double Length { get; set; } = 1;
    [ObservableProperty] public partial int ScopeIndex { get; set; }
    [ObservableProperty] public partial int CaseIndex { get; set; }
    [ObservableProperty] public partial double Start { get; set; } = 1;
    [ObservableProperty] public partial double Step { get; set; } = 1;
    [ObservableProperty] public partial double Padding { get; set; } = 3;
    [ObservableProperty] public partial string Separator { get; set; } = "_";
    [ObservableProperty] public partial bool IsFolderMode { get; set; }
    public bool CanChooseScope => !IsFolderMode;
    public bool IsSpecificOccurrence => OccurrenceMode == 1;
    public string ScopeHint => IsFolderMode ? "文件夹始终处理完整名称，包括名称中的点。" : "文件名：保留当前扩展名；文件名＋扩展名：处理完整名称。";
    public string Summary => Method switch
    {
        RenameMethod.Replace => $"“{Text}” → “{Replacement}” · {(OccurrenceMode == 0 ? "全部匹配" : $"第 {OccurrenceNumber} 次匹配")}",
        RenameMethod.Insert => $"从{(FromRight ? "右" : "左")}侧位置 {Position} 添加“{Text}”",
        RenameMethod.DeleteRange => $"从{(FromRight ? "右" : "左")}侧跳过 {Position} 个字符，删除 {Length} 个",
        RenameMethod.RemoveCharacters => $"删除字符集合中的字符：“{Text}”",
        RenameMethod.ChangeCase => CaseIndex switch { 1 => "全部大写", 2 => "单词首字母大写", _ => "全部小写" },
        _ => $"起始 {Start} · 步长 {Step} · {Padding} 位 · 位置 {Position}",
    };

    public RenameMethodViewModel(RenameRule rule)
    {
        Id = rule.Id; Method = rule.Method; Enabled = rule.Enabled; Text = rule.Text;
        Replacement = rule.Replacement; MatchCase = rule.MatchCase;
        OccurrenceMode = rule.Occurrence == 0 ? 0 : 1; OccurrenceNumber = Math.Max(1, rule.Occurrence);
        Position = rule.Position; FromRight = rule.FromRight; Length = rule.Length;
        ScopeIndex = (int)rule.Scope; CaseIndex = (int)rule.Case;
        Start = rule.Start; Step = rule.Step; Padding = rule.Padding; Separator = rule.Separator;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Order)) { OnPropertyChanged(nameof(Title)); return; }
            if (e.PropertyName == nameof(IsFolderMode))
            { OnPropertyChanged(nameof(CanChooseScope)); OnPropertyChanged(nameof(ScopeHint)); return; }
            if (e.PropertyName is nameof(IsExpanded) or nameof(IsSelected) or nameof(Summary) or nameof(Title) or nameof(CanChooseScope) or nameof(ScopeHint) or nameof(IsSpecificOccurrence)) return;
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(IsSpecificOccurrence));
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public RenameRule Snapshot() => new()
    {
        Id = Id, Method = Method, Enabled = Enabled, Text = Text, Replacement = Replacement,
        MatchCase = MatchCase, Scope = (NameScope)ScopeIndex, Case = (LetterCase)CaseIndex,
        Occurrence = OccurrenceMode == 0 ? 0 : Integer(OccurrenceNumber, "匹配次数", 1),
        Position = Integer(Position, "索引位置", 0), Length = Integer(Length, "删除长度", 1),
        Start = Integer(Start, "起始序号", 0), Step = Integer(Step, "步长", 0),
        Padding = Integer(Padding, "序号位数", 0, 32), FromRight = FromRight, Separator = Separator,
    };

    private static int Integer(double value, string name, int minimum, int maximum = int.MaxValue)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < minimum || value > maximum)
            throw new ArgumentException($"{name}必须为 {minimum}～{maximum} 的整数。");
        return (int)value;
    }
}
