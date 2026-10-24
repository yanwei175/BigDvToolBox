using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace BigDvToolBox_App.Features.PdfLayOut;

internal sealed record PdfLayOutTicketFeature(string Name, string Namespace, string[] Options, IReadOnlyDictionary<string, string?> Values);
internal sealed record PdfLayOutTicketParameter(string Name, string Namespace, string Value);

/// <summary>只读取票据，不能把修改返回的流误当作修改了驱动设置。</summary>
internal sealed record PdfLayOutPrintTicket(
    string Xml, IReadOnlyList<PdfLayOutTicketFeature> Features, IReadOnlyList<PdfLayOutTicketParameter> Parameters,
    string ScalingStatus, string? BlockingReason, PdfLayOutRect? ImageableAreaDip)
{
    internal const int MaximumBytes = 2 * 1024 * 1024;
    private const string Framework = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
    private const string Keywords = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";

    public static PdfLayOutPrintTicket Parse(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes,
        });
        var xml = XDocument.Load(reader);
        bool IsFramework(XElement e, string local) => e.Name.LocalName == local && Normalize(e.Name.NamespaceName) == Framework;
        var parameters = xml.Descendants().Where(e => IsFramework(e, "ParameterInit"))
            .Select(e => (Key: Name(e), Value: e.Elements().FirstOrDefault(v => IsFramework(v, "Value"))?.Value ?? string.Empty))
            .ToArray();
        var values = parameters.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        string? ReadValue(XElement property)
        {
            if (property.Elements().FirstOrDefault(e => IsFramework(e, "Value")) is { } value) return value.Value;
            var reference = property.Elements().FirstOrDefault(e => IsFramework(e, "ParameterRef"));
            return reference is not null && values.TryGetValue(Name(reference), out var resolved) ? resolved : null;
        }
        var features = xml.Descendants().Where(e => IsFramework(e, "Feature")).Select(feature =>
        {
            var name = Name(feature);
            var options = feature.Elements().Where(e => IsFramework(e, "Option")).ToArray();
            var properties = options.SelectMany(o => o.Elements().Where(e => IsFramework(e, "ScoredProperty") || IsFramework(e, "Property")))
                .GroupBy(p => Name(p)).ToDictionary(g => Expanded(g.Key), g => ReadValue(g.Last()));
            return new PdfLayOutTicketFeature(name.LocalName, name.NamespaceName, options.Select(o => Expanded(Name(o))).ToArray(), properties);
        }).ToArray();
        string status = "NotSpecified";
        string? blocked = null;
        foreach (var feature in features.Where(f => f.Namespace == Keywords && f.Name == "PageScaling"))
        {
            bool none = feature.Options.Length == 1 && feature.Options[0] == Expanded(XName.Get("None", Keywords));
            bool custom = feature.Options.Length == 1 &&
                (feature.Options[0] == Expanded(XName.Get("Custom", Keywords)) || feature.Options[0] == Expanded(XName.Get("CustomSquare", Keywords)));
            var required = feature.Options.FirstOrDefault() == Expanded(XName.Get("CustomSquare", Keywords)) ? new[] { "Scale" } : new[] { "ScaleWidth", "ScaleHeight" };
            bool custom100 = custom && required.All(p => feature.Values.TryGetValue(Expanded(XName.Get(p, Keywords)), out string? value) &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && Math.Abs(number - 100) < 0.000001);
            status = none ? "None" : custom100 ? "Custom100Percent" : "ScalingEnabledOrUnrecognized";
            if (!none && !custom100)
                blocked ??= "驱动打印票据启用了缩放或无法确认的缩放方式。请在打印首选项/更多设置中选择实际大小（100%），关闭适应纸张后重试。为保持拼版尺寸，本次未提交页面。";
        }
        foreach (var feature in features.Where(f => f.Namespace == Keywords && f.Name.Contains("NUp", StringComparison.Ordinal)))
            if (feature.Values.TryGetValue(Expanded(XName.Get("PagesPerSheet", Keywords)), out string? value) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 1)
                blocked ??= "驱动启用了多页合一，请设置为每张纸一页后重试。为保持拼版尺寸，本次未提交页面。";

        // 部分驱动会附带可打印区属性；没有时不能臆造一个固定的 3 mm 边距。
        var imageable = xml.Descendants().FirstOrDefault(e => IsFramework(e, "Property") && Name(e) == XName.Get("PageImageableSize", Keywords));
        PdfLayOutRect? area = null;
        if (imageable is not null)
        {
            var properties = imageable.Descendants().Where(e => IsFramework(e, "Property"))
                .GroupBy(e => Name(e)).ToDictionary(g => g.Key, g => ReadValue(g.Last()));
            double? Millimeters(string name) => properties.TryGetValue(XName.Get(name, Keywords), out var value) &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var microns) && double.IsFinite(microns) ? microns / 1000 : null;
            if (Millimeters("OriginWidth") is { } x && Millimeters("OriginHeight") is { } y &&
                Millimeters("ExtentWidth") is { } w && Millimeters("ExtentHeight") is { } h && w > 0 && h > 0)
                area = new(x * 96 / 25.4, y * 96 / 25.4, w * 96 / 25.4, h * 96 / 25.4);
        }
        return new(xml.ToString(SaveOptions.DisableFormatting), features,
            parameters.Select(p => new PdfLayOutTicketParameter(p.Key.LocalName, p.Key.NamespaceName, p.Value)).ToArray(), status, blocked, area);
    }

    public void EnsureActualSize()
    {
        if (BlockingReason is not null) throw new InvalidOperationException(BlockingReason);
    }

    private static string Normalize(string ns) => ns.Replace("https://schemas.microsoft.com/", "http://schemas.microsoft.com/", StringComparison.Ordinal);
    private static XName Name(XElement element)
    {
        string value = element.Attribute("name")?.Value ?? string.Empty;
        int colon = value.IndexOf(':');
        string local = colon >= 0 ? value[(colon + 1)..] : value;
        string ns = colon >= 0 ? element.GetNamespaceOfPrefix(value[..colon])?.NamespaceName ?? string.Empty : element.GetDefaultNamespace().NamespaceName;
        return XName.Get(local.Length == 0 ? "Unnamed" : local, Normalize(ns));
    }
    private static string Expanded(XName name) => name.ToString();
}
