namespace BigDvToolBox_App.Features.PdfLayOut;

// 领域模型统一使用毫米，不依赖 WinUI、PDFsharp 文档实例或预览缩放比例。
public readonly record struct PdfLayOutSize(double Width, double Height)
{
    public bool IsValid => double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
}

public readonly record struct PdfLayOutInsets(double Left, double Top, double Right, double Bottom)
{
    public bool IsValid => new[] { Left, Top, Right, Bottom }.All(value => double.IsFinite(value) && value >= 0);
}

public readonly record struct PdfLayOutOffset(double X, double Y);
public readonly record struct PdfLayOutPoint(double X, double Y);

/// <summary>印张左上角为原点，X 向右、Y 向下；所有坐标和长度均为毫米。</summary>
public readonly record struct PdfLayOutRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PdfLayOutRect Intersect(PdfLayOutRect other)
    {
        double left = Math.Max(X, other.X), top = Math.Max(Y, other.Y);
        return new(left, top, Math.Max(0, Math.Min(Right, other.Right) - left),
            Math.Max(0, Math.Min(Bottom, other.Bottom) - top));
    }
}

public readonly record struct PdfLayOutLine(PdfLayOutPoint Start, PdfLayOutPoint End);
public enum PdfLayOutMarkKind { Crop, CellBorder }

/// <summary>可直接绘制的印刷标记；线宽、端点均为毫米，使用黑色实线。</summary>
public sealed record PdfLayOutPrintMark(
    PdfLayOutMarkKind Kind,
    double StrokeWidth,
    IReadOnlyList<PdfLayOutLine> Lines);

/// <summary>源页索引从 0 开始，尺寸为应用页面旋转后的可见页面尺寸。</summary>
public sealed record PdfLayOutSourcePage(int PageIndex, PdfLayOutSize Size)
{
    public int PageNumber => PageIndex + 1;
}

/// <summary>预览只持有当前页的 PNG 数据，不把 WinUI 图像对象放进 ViewModel。</summary>
public sealed record PdfLayOutPagePreview(int PageIndex, byte[] PngBytes, uint PixelWidth, uint PixelHeight);

/// <summary>仅保存元数据。PDF 文件的打开、关闭和渲染由服务管理。</summary>
public sealed class PdfLayOutDocument
{
    public PdfLayOutDocument(string filePath, IEnumerable<PdfLayOutSourcePage> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(pages);
        PdfLayOutSourcePage[] snapshot = pages.ToArray();
        if (snapshot.Length == 0 || snapshot.Where((page, index) => page is null || page.PageIndex != index || !page.Size.IsValid).Any())
        {
            throw new ArgumentException("PDF 必须包含尺寸有效、索引连续的页面。", nameof(pages));
        }

        FilePath = Path.GetFullPath(filePath);
        Pages = Array.AsReadOnly(snapshot);
    }

    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);
    public IReadOnlyList<PdfLayOutSourcePage> Pages { get; }
    public int PageCount => Pages.Count;
}

public enum PdfLayOutGridMode { Automatic, Manual }
public enum PdfLayOutScaleMode { OriginalSize, Fit, Fill, Custom }
public enum PdfLayOutOperation { Idle, Loading, Exporting, Printing }
public enum PdfLayOutPrintResult { Submitted, Cancelled }

public sealed record PdfLayOutMarkSettings
{
    public bool ShowCropMarks { get; init; } = true;
    public bool ShowInnerMarks { get; init; } = true;
    public double Length { get; init; } = 2.8;
    public double Offset { get; init; } = 1.1;
    public bool ShowCellBorders { get; init; }
}

/// <summary>不可变的版式快照，打印和导出使用同一组参数。当前仅支持重复拼版。</summary>
public sealed record PdfLayOutSettings
{
    public PdfLayOutSize SheetSize { get; init; } = new(210, 297);
    public PdfLayOutInsets SheetMargins { get; init; }
    public PdfLayOutInsets SourceCrop { get; init; }
    /// <summary>标签相对容器内居中位置的位移；不改变容器的边距、间距或尺寸。</summary>
    public PdfLayOutOffset Offset { get; init; }
    public PdfLayOutGridMode GridMode { get; init; } = PdfLayOutGridMode.Automatic;
    public int Columns { get; init; } = 2;
    public int Rows { get; init; } = 2;
    public double HorizontalGap { get; init; } = 5;
    public double VerticalGap { get; init; } = 5;
    public PdfLayOutScaleMode ScaleMode { get; init; } = PdfLayOutScaleMode.OriginalSize;
    public double ScalePercent { get; init; } = 100;
    public PdfLayOutMarkSettings Marks { get; init; } = new();
}

/// <summary>
/// SourceBounds 是源可见页面中的裁切矩形；其余矩形均为印张坐标。
/// ImageBounds 为整张源页的绘制位置，ContentBounds 为裁切区域缩放后的位置，
/// CellBounds 为固定容器，TrimBounds 将标签限制到容器，ClipBounds 最后限制到纸张。
/// Scale 为无单位的等比缩放倍数。
/// 预览和导出均在 ClipBounds 内绘制 ImageBounds，不再单独推算裁切或居中。
/// </summary>
public sealed record PdfLayOutPlacement(
    int SourcePageIndex,
    int Row,
    int Column,
    PdfLayOutRect CellBounds,
    PdfLayOutRect ContentBounds,
    PdfLayOutRect SourceBounds)
{
    public double Scale { get; init; }
    public PdfLayOutRect ImageBounds { get; init; }
    public PdfLayOutRect TrimBounds { get; init; }
    public PdfLayOutRect ClipBounds { get; init; }
}

public sealed record PdfLayOutSheetLayout(
    int SourcePageIndex,
    PdfLayOutSize SheetSize,
    PdfLayOutSize CroppedPageSize,
    int Rows,
    int Columns,
    IReadOnlyList<PdfLayOutPlacement> Placements,
    string? ErrorMessage)
{
    public bool IsValid => ErrorMessage is null && Placements.Count > 0;
    public PdfLayOutRect SheetBounds => new(0, 0, SheetSize.Width, SheetSize.Height);
    public PdfLayOutRect GridBounds { get; init; }
    public double HorizontalGap { get; init; }
    public double VerticalGap { get; init; }
    public IReadOnlyList<PdfLayOutPrintMark> Marks { get; init; } = [];
}

/// <summary>一次性解析全部印张。输出只消费 Sheets 中的几何数据，后续界面修改不会改变任务。</summary>
public sealed class PdfLayOutJob
{
    public PdfLayOutJob(PdfLayOutDocument document, PdfLayOutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(settings);
        var sheets = new List<PdfLayOutSheetLayout>(document.PageCount);
        foreach (PdfLayOutSourcePage page in document.Pages)
        {
            PdfLayOutSheetLayout sheet = PdfLayOutCalculator.Calculate(page, settings);
            if (!sheet.IsValid)
                throw new InvalidOperationException($"第 {page.PageIndex + 1} 页：{sheet.ErrorMessage}");
            sheets.Add(sheet);
        }
        Document = document;
        Settings = settings;
        Sheets = sheets.AsReadOnly();
    }

    public PdfLayOutDocument Document { get; }
    public PdfLayOutSettings Settings { get; }
    public IReadOnlyList<PdfLayOutSheetLayout> Sheets { get; }
}
