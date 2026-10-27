using System.Globalization;
using System.Text;

namespace BigDvToolBox.Renaming.Core;

public enum RenameMethod { Replace, Insert, DeleteRange, RemoveCharacters, ChangeCase, Numbering }
public enum NameScope { Stem, FullName }
public enum LetterCase { Lower, Upper, Title }
public enum EntryKind { File, Folder }

/// <summary>不可变的方法快照；不持有控件、文件句柄或可变的批次计数。</summary>
public sealed record RenameRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public RenameMethod Method { get; init; }
    public bool Enabled { get; init; } = true;
    public NameScope Scope { get; init; }
    public string Text { get; init; } = "";
    public string Replacement { get; init; } = "";
    public bool MatchCase { get; init; }
    public int Occurrence { get; init; } // 0 = 全部，1 = 第一次匹配。
    public int Position { get; init; }
    public bool FromRight { get; init; }
    public int Length { get; init; } = 1;
    public LetterCase Case { get; init; }
    public int Start { get; init; } = 1;
    public int Step { get; init; } = 1;
    public int Padding { get; init; } = 3;
    public string Separator { get; init; } = "_";

    public string? Validate()
    {
        if (!Enum.IsDefined(Method) || !Enum.IsDefined(Scope) || !Enum.IsDefined(Case)) return "方法或参数类型无效。";
        if (Text is null || Replacement is null || Separator is null) return "文字参数不能为空值。";
        if (Method is RenameMethod.Replace or RenameMethod.RemoveCharacters && Text.Length == 0) return "请输入查找内容或待删除字符。";
        if (Occurrence < 0 || Position < 0 || Length < 0) return "匹配次数、索引和长度不能小于零。";
        if (Method == RenameMethod.DeleteRange && Length == 0) return "删除长度必须大于零。";
        if (Padding is < 0 or > 32) return "序号位数应在 0～32 之间。";
        return null;
    }

    public string Apply(string currentName, EntryKind kind, int batchIndex)
    {
        if (!Enabled) return currentName;
        if (Validate() is { } error) throw new ArgumentException(error);
        // 每一步以当前名称为输入；文件夹中的点永远不作为扩展名。
        string extension = kind == EntryKind.File && Scope == NameScope.Stem ? Path.GetExtension(currentName) : "";
        string value = extension.Length == 0 ? currentName : currentName[..^extension.Length];
        string result = Method switch
        {
            RenameMethod.Replace => Replace(value),
            RenameMethod.Insert => Insert(value, Text),
            RenameMethod.DeleteRange => Delete(value),
            RenameMethod.RemoveCharacters => RemoveCharacters(value),
            RenameMethod.ChangeCase => Case switch
            {
                LetterCase.Upper => value.ToUpperInvariant(),
                LetterCase.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant()),
                _ => value.ToLowerInvariant(),
            },
            RenameMethod.Numbering => Insert(value, NumberText(batchIndex)),
            _ => throw new ArgumentException("未知的重命名方法。"),
        };
        return result + extension;
    }

    private string Replace(string value)
    {
        var comparison = MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (Occurrence == 0) return value.Replace(Text, Replacement, comparison);
        int offset = 0;
        for (int i = 1; ; i++)
        {
            int found = value.IndexOf(Text, offset, comparison);
            if (found < 0) return value;
            if (i == Occurrence) return value[..found] + Replacement + value[(found + Text.Length)..];
            offset = found + Text.Length;
        }
    }

    private string NumberText(int batchIndex)
    {
        string number = checked((long)Start + (long)batchIndex * Step)
            .ToString(CultureInfo.InvariantCulture).PadLeft(Padding, '0');
        return FromRight ? Separator + number : number + Separator;
    }

    private string Insert(string value, string text)
    {
        int[] elements = StringInfo.ParseCombiningCharacters(value);
        if (Position > elements.Length) throw new ArgumentException("插入位置超出名称长度。常用位置：0 表示开头，从右侧定位的 0 表示末尾。");
        int index = FromRight ? elements.Length - Position : Position;
        return value.Insert(index == elements.Length ? value.Length : elements[index], text);
    }

    private string Delete(string value)
    {
        int[] elements = StringInfo.ParseCombiningCharacters(value);
        if ((long)Position + Length > elements.Length) throw new ArgumentException("删除范围超出名称长度。索引表示从指定方向跳过的字符数。");
        int start = FromRight ? elements.Length - Position - Length : Position;
        int end = start + Length;
        int charStart = start == elements.Length ? value.Length : elements[start];
        int charEnd = end == elements.Length ? value.Length : elements[end];
        return value.Remove(charStart, charEnd - charStart);
    }

    private string RemoveCharacters(string value)
    {
        var blocked = new HashSet<string>(Elements(Text), StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (string element in Elements(value)) if (!blocked.Contains(element)) builder.Append(element);
        return builder.ToString();
    }

    private static IEnumerable<string> Elements(string value)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext()) yield return enumerator.GetTextElement();
    }
}
