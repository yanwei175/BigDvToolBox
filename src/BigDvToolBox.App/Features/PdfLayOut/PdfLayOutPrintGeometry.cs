namespace BigDvToolBox_App.Features.PdfLayOut;

internal readonly record struct PdfLayOutPrintPageRange(uint From, uint To);
internal readonly record struct PdfLayOutPrintRasterSize(int Width, int Height);
internal sealed record PdfLayOutPrintVisualPlan(PdfLayOutSize Paper, PdfLayOutRect Sheet)
{
    public PdfLayOutRect PrintableArea { get; init; }
    public PdfLayOutRect ContentClip { get; init; }
    public IReadOnlyList<PdfLayOutRect> UnprintableAreas { get; init; } = [];
    public bool HasHardwareMargins => UnprintableAreas.Count > 0;
    /// <summary>从版式毫米坐标映射到整纸 DIP；不以可打印区域左上角作为新原点。</summary>
    public PdfLayOutPoint SheetMillimetersToPaperDip(PdfLayOutPoint point) =>
        new(Sheet.X + point.X * 96 / 25.4, Sheet.Y + point.Y * 96 / 25.4);
}

/// <summary>物理毫米、打印页 DIP 与栅格像素的转换。分辨率限制不改变纸上的尺寸。</summary>
internal static class PdfLayOutPrintGeometry
{
    internal const int MaximumRasterDpi = 600;
    internal const int MaximumRasterEdge = 16000;
    internal const long MaximumRasterPixels = 24_000_000;
    public static PdfLayOutPrintVisualPlan CreateVisualPlan(PdfLayOutSize sheet, PdfLayOutSize paperInDip,
        PdfLayOutRect? imageableArea = null)
    {
        if (!sheet.IsValid || !paperInDip.IsValid) throw new InvalidOperationException("印张或打印纸张尺寸无效。");
        double width = sheet.Width * 96 / 25.4, height = sheet.Height * 96 / 25.4;
        var paperBounds = new PdfLayOutRect(0, 0, paperInDip.Width, paperInDip.Height);
        var printable = imageableArea ?? paperBounds;
        if (!double.IsFinite(printable.X) || !double.IsFinite(printable.Y) ||
            !double.IsFinite(printable.Width) || !double.IsFinite(printable.Height) || printable.IsEmpty)
            throw new InvalidOperationException("驱动返回的可打印区域无效。");
        printable = printable.Intersect(paperBounds);
        if (printable.IsEmpty) throw new InvalidOperationException("驱动返回的可打印区域不在纸张范围内。");
        var placement = new PdfLayOutRect((paperInDip.Width - width) / 2, (paperInDip.Height - height) / 2, width, height);
        PdfLayOutRect[] masks =
        [
            new(0, 0, paperInDip.Width, printable.Y),
            new(0, printable.Bottom, paperInDip.Width, paperInDip.Height - printable.Bottom),
            new(0, printable.Y, printable.X, printable.Height),
            new(printable.Right, printable.Y, paperInDip.Width - printable.Right, printable.Height),
        ];
        return new(paperInDip, placement)
        {
            PrintableArea = printable, ContentClip = placement.Intersect(printable),
            UnprintableAreas = masks.Where(r => !r.IsEmpty).ToArray(),
        };
    }

    public static PdfLayOutPrintRasterSize CreateRasterSize(PdfLayOutSize sheet, double dpi)
    {
        if (!sheet.IsValid || !double.IsFinite(dpi) || dpi <= 0) throw new InvalidOperationException("印张尺寸或打印分辨率无效。");
        double rasterDpi = Math.Min(dpi, MaximumRasterDpi);
        double rasterWidth = sheet.Width / 25.4 * rasterDpi;
        double rasterHeight = sheet.Height / 25.4 * rasterDpi;
        double scale = Math.Min(1, Math.Min(MaximumRasterEdge / Math.Max(rasterWidth, rasterHeight),
            Math.Sqrt(MaximumRasterPixels / (rasterWidth * rasterHeight))));
        return new PdfLayOutPrintRasterSize(Math.Max(1, (int)Math.Floor(rasterWidth * scale)),
            Math.Max(1, (int)Math.Floor(rasterHeight * scale)));
    }

    public static IReadOnlyList<int> SelectPages(int pageCount, IReadOnlyList<PdfLayOutPrintPageRange>? ranges)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        if (ranges is null) return Enumerable.Range(0, pageCount).ToArray();
        var pages = new SortedSet<int>();
        foreach (PdfLayOutPrintPageRange range in ranges)
        {
            if (range.From < 1 || range.To < range.From || range.To > pageCount)
                throw new InvalidOperationException("打印页码范围无效。");
            for (uint page = range.From; page <= range.To; page++) pages.Add(checked((int)page - 1));
        }
        if (pages.Count == 0) throw new InvalidOperationException("请选择需要打印的页码。");
        return pages.ToArray();
    }
}
