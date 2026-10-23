using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>检查页面、嵌套 Form、Pattern、软蒙版和批注外观中的字体资源，不依赖本机字体。</summary>
internal static class PdfLayOutFontInspector
{
    internal static IReadOnlyList<PdfLayOutFontIssue> Inspect(PdfDocument document, CancellationToken token)
    {
        var issues = new SortedDictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        for (int pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            token.ThrowIfCancellationRequested();
            int pageNumber = pageIndex + 1;
            var visited = new HashSet<PdfItem>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<PdfItem>();
            PdfDictionary page = document.Pages[pageIndex];
            // Resources 可以从 Pages 节点继承，不能只检查页面自己的字典。
            var ancestors = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            for (PdfDictionary? ancestor = page; ancestor is not null && ancestors.Add(ancestor);
                 ancestor = ancestor.Elements.GetDictionary("/Parent"))
            {
                if (ancestor.Elements.GetDictionary("/Resources") is not { } resources) continue;
                pending.Push(resources);
                break;
            }
            if (page.Elements.GetArray("/Annots") is { } annotations)
                foreach (PdfItem annotation in annotations.Elements)
                    if (Resolve(annotation) is PdfDictionary dictionary && dictionary.Elements.GetDictionary("/AP") is { } appearance)
                        pending.Push(appearance);

            while (pending.TryPop(out PdfItem? item))
            {
                token.ThrowIfCancellationRequested();
                item = Resolve(item);
                if (!visited.Add(item)) continue;
                if (item is PdfArray array)
                {
                    foreach (PdfItem child in array.Elements) pending.Push(child);
                }
                else if (item is PdfDictionary dictionary)
                {
                    string type = Name(dictionary, "/Type");
                    // 资源名本身也可以叫 /P 或 /Type；只跳过真正的页面反向引用。
                    if (type is "/Page" or "/Pages") continue;
                    string subtype = Name(dictionary, "/Subtype");
                    bool isFont = type == "/Font" ||
                        subtype is "/Type0" or "/Type1" or "/MMType1" or "/TrueType" or "/Type3" or "/CIDFontType0" or "/CIDFontType2";
                    if (isFont)
                    {
                        if (!IsEmbedded(dictionary))
                        {
                            string name = Name(dictionary, "/BaseFont").TrimStart('/');
                            if (string.IsNullOrWhiteSpace(name)) name = "未命名字体";
                            if (!issues.TryGetValue(name, out SortedSet<int>? pages)) issues[name] = pages = [];
                            pages.Add(pageNumber);
                        }
                        // Type 3 的字形可以引用其他字体；Type 0 的后代已经由 IsEmbedded 检查。
                        if (dictionary.Elements.GetDictionary("/Resources") is { } resources) pending.Push(resources);
                        if (dictionary.Elements.GetDictionary("/CharProcs") is { } glyphs) pending.Push(glyphs);
                        continue;
                    }
                    foreach (string key in dictionary.Elements.Keys)
                    {
                        if (dictionary.Elements[key] is { } child) pending.Push(child);
                    }
                }
            }
        }
        return issues.Select(pair => new PdfLayOutFontIssue(pair.Key, Array.AsReadOnly(pair.Value.ToArray()))).ToArray();
    }

    private static PdfItem Resolve(PdfItem item) => item is PdfReference reference ? reference.Value : item;

    private static string Name(PdfDictionary dictionary, string key) =>
        dictionary.Elements[key] is { } item && Resolve(item) is PdfName name ? name.Value : string.Empty;

    private static bool IsEmbedded(PdfDictionary font)
    {
        string subtype = Name(font, "/Subtype");
        // Type 3 将字形定义直接写入 CharProcs，无需外部字体文件。
        if (subtype == "/Type3") return true;
        if (subtype == "/Type0")
        {
            PdfArray? descendants = font.Elements.GetArray("/DescendantFonts");
            return descendants is { Elements.Count: > 0 } && descendants.Elements.Cast<PdfItem>()
                .All(item => Resolve(item) is PdfDictionary child && HasFontProgram(child));
        }
        return HasFontProgram(font);
    }

    private static bool HasFontProgram(PdfDictionary font) =>
        font.Elements.GetDictionary("/FontDescriptor") is { } descriptor &&
        new[] { "/FontFile", "/FontFile2", "/FontFile3" }.Any(key => descriptor.Elements.GetDictionary(key)?.Stream is not null);
}
