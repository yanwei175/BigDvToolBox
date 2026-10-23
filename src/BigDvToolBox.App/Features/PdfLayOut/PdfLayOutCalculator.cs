namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>
/// 纯毫米几何计算。重复和自由模式共用容器、页面绘制位置及裁切规则。
/// 不打开文件、不创建 UI，也不依赖 PDFsharp，保证三个输出入口可以共用规则。
/// </summary>
public static partial class PdfLayOutCalculator
{
    public const int MaximumGridCount = 100;

    public static PdfLayOutSheetLayout Calculate(PdfLayOutSourcePage page, PdfLayOutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(settings);
        PdfLayOutSheetLayout Invalid(string message) => new(page.PageIndex, settings.SheetSize, default, 0, 0, [], message);

        if (!page.Size.IsValid || !settings.SheetSize.IsValid)
            return Invalid("源页面和印张尺寸必须为有效的正数。");
        if (!settings.SheetMargins.IsValid || !settings.SourceCrop.IsValid ||
            !IsNonNegative(settings.HorizontalGap) || !IsNonNegative(settings.VerticalGap))
            return Invalid("边距、裁切和间距必须为有效的非负数。");
        if (!double.IsFinite(settings.Offset.X) || !double.IsFinite(settings.Offset.Y))
            return Invalid("标签位移必须为有效数值。");
        if (!Enum.IsDefined(settings.Mode) || !Enum.IsDefined(settings.ScaleMode) || !Enum.IsDefined(settings.GridMode) ||
            settings.Columns is < 1 or > MaximumGridCount || settings.Rows is < 1 or > MaximumGridCount)
            return Invalid("网格与缩放设置无效。");
        if (settings.Marks is null || !IsNonNegative(settings.Marks.Length) || !IsNonNegative(settings.Marks.Offset))
            return Invalid("印刷标记的长度和离角距离必须为有效的非负数。");
        if (settings.ScaleMode == PdfLayOutScaleMode.Custom &&
            (!double.IsFinite(settings.ScalePercent) || settings.ScalePercent is <= 0 or > 1000))
            return Invalid("自定义缩放比例必须大于 0 且不超过 1000%。");

        var crop = settings.SourceCrop;
        var cropped = new PdfLayOutSize(page.Size.Width - crop.Left - crop.Right, page.Size.Height - crop.Top - crop.Bottom);
        if (!cropped.IsValid)
            return Invalid("源页面裁切后没有可用区域。");

        var margins = settings.SheetMargins;
        double availableWidth = settings.SheetSize.Width - margins.Left - margins.Right;
        double availableHeight = settings.SheetSize.Height - margins.Top - margins.Bottom;
        if (availableWidth <= 0 || availableHeight <= 0)
            return Invalid("印张页边距超出了纸张尺寸。");

        // 自动模式按裁切后的原始尺寸选行列；缩放只作用于已确定的容器内。
        int columns = settings.GridMode == PdfLayOutGridMode.Automatic
            ? FitCount(availableWidth, cropped.Width, settings.HorizontalGap) : settings.Columns;
        int rows = settings.GridMode == PdfLayOutGridMode.Automatic
            ? FitCount(availableHeight, cropped.Height, settings.VerticalGap) : settings.Rows;
        if (rows == 0 || columns == 0)
            return Invalid("当前纸张的可用区域无法容纳源页面，请调整纸张尺寸、裁切或改用手动网格。");

        double cellWidth = (availableWidth - (columns - 1) * settings.HorizontalGap) / columns;
        double cellHeight = (availableHeight - (rows - 1) * settings.VerticalGap) / rows;
        if (cellWidth <= 0 || cellHeight <= 0)
            return Invalid("页面间距超出了印张可用区域。");

        var sheetBounds = new PdfLayOutRect(0, 0, settings.SheetSize.Width, settings.SheetSize.Height);
        // 容器位置只受纸张边距和网格间距影响，标签位移不能移动容器。
        var gridBounds = new PdfLayOutRect(margins.Left, margins.Top, availableWidth, availableHeight);
        var placements = new List<PdfLayOutPlacement>(rows * columns);
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                double x = gridBounds.X + column * (cellWidth + settings.HorizontalGap);
                double y = gridBounds.Y + row * (cellHeight + settings.VerticalGap);
                var cell = new PdfLayOutRect(x, y, cellWidth, cellHeight);
                placements.Add(CreatePlacement(page, settings, row, column, cell));
            }
        }

        return new(page.PageIndex, settings.SheetSize, cropped, rows, columns, placements.AsReadOnly(), null)
        {
            GridBounds = gridBounds,
            HorizontalGap = settings.HorizontalGap,
            VerticalGap = settings.VerticalGap,
            Marks = PdfLayOutMarkCalculator.Calculate(placements, rows, columns, sheetBounds, settings.Marks),
        };
    }

    public static int NormalizeGridCount(double value) => double.IsFinite(value)
        ? (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 1, MaximumGridCount) : 1;

    private static bool IsNonNegative(double value) => double.IsFinite(value) && value >= 0;

    private static int FitCount(double available, double pageSize, double gap) =>
        // PDF point、DIP 和毫米换算存在浮点误差，允许 0.001 mm 的尺寸误差。
        (int)Math.Clamp(Math.Floor((available + gap + 0.001) / (pageSize + gap)), 0, MaximumGridCount);
}
