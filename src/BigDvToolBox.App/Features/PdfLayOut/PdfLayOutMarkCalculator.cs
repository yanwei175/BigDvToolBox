namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>生成可直接绘制的毫米标记和容器边框。裁切标记位于容器外留白。</summary>
internal static class PdfLayOutMarkCalculator
{
    private const double StrokeWidth = 0.1;

    public static IReadOnlyList<PdfLayOutPrintMark> Calculate(
        IReadOnlyList<PdfLayOutPlacement> cells, int rows, int columns,
        PdfLayOutRect sheet, PdfLayOutMarkSettings settings)
    {
        var marks = new List<PdfLayOutPrintMark>();
        if (settings.ShowCropMarks && settings.Length > 0)
        {
            var lines = new HashSet<PdfLayOutLine>();
            foreach (PdfLayOutPlacement cell in cells)
            {
                // 裁切的是固定容器，标记不能跟随标签的缩放、源裁切或位移。
                PdfLayOutRect bounds = cell.CellBounds;
                if (settings.ShowInnerMarks || cell.Column == 0)
                    Horizontal(bounds.X, -1, cell.Column == 0 ? bounds.X - sheet.X - StrokeWidth / 2
                        : (bounds.X - cells[cell.Row * columns + cell.Column - 1].CellBounds.Right) / 2);
                if (settings.ShowInnerMarks || cell.Column == columns - 1)
                    Horizontal(bounds.Right, 1, cell.Column == columns - 1 ? sheet.Right - bounds.Right - StrokeWidth / 2
                        : (cells[cell.Row * columns + cell.Column + 1].CellBounds.X - bounds.Right) / 2);
                if (settings.ShowInnerMarks || cell.Row == 0)
                    Vertical(bounds.Y, -1, cell.Row == 0 ? bounds.Y - sheet.Y - StrokeWidth / 2
                        : (bounds.Y - cells[(cell.Row - 1) * columns + cell.Column].CellBounds.Bottom) / 2);
                if (settings.ShowInnerMarks || cell.Row == rows - 1)
                    Vertical(bounds.Bottom, 1, cell.Row == rows - 1 ? sheet.Bottom - bounds.Bottom - StrokeWidth / 2
                        : (cells[(cell.Row + 1) * columns + cell.Column].CellBounds.Y - bounds.Bottom) / 2);

                // 内部最多占用间隙的一半，外缘最多使用实际纸张边距；放不下时省略。
                // 只缩短长度，不向容器内部挪动标记，离角距离始终保持原值。
                void Horizontal(double edge, int direction, double room)
                {
                    double length = Math.Min(settings.Length, room - settings.Offset);
                    if (length <= 0) return;
                    double start = edge + direction * settings.Offset;
                    double end = start + direction * length;
                    AddHorizontal(Math.Min(start, end), Math.Max(start, end), bounds.Y);
                    AddHorizontal(Math.Min(start, end), Math.Max(start, end), bounds.Bottom);
                }

                void Vertical(double edge, int direction, double room)
                {
                    double length = Math.Min(settings.Length, room - settings.Offset);
                    if (length <= 0) return;
                    double start = edge + direction * settings.Offset;
                    double end = start + direction * length;
                    AddVertical(bounds.X, Math.Min(start, end), Math.Max(start, end));
                    AddVertical(bounds.Right, Math.Min(start, end), Math.Max(start, end));
                }
            }
            if (lines.Count > 0)
                marks.Add(new(PdfLayOutMarkKind.Crop, StrokeWidth, Array.AsReadOnly(lines.ToArray())));

            // 将整段线（包括线宽）限制在纸内；同位置的线只输出一次。
            void AddHorizontal(double left, double right, double y)
            {
                left = Math.Max(left, sheet.X + StrokeWidth / 2);
                right = Math.Min(right, sheet.Right - StrokeWidth / 2);
                if (right > left && y >= sheet.Y + StrokeWidth / 2 && y <= sheet.Bottom - StrokeWidth / 2)
                    lines.Add(new(Point(left, y), Point(right, y)));
            }

            void AddVertical(double x, double top, double bottom)
            {
                top = Math.Max(top, sheet.Y + StrokeWidth / 2);
                bottom = Math.Min(bottom, sheet.Bottom - StrokeWidth / 2);
                if (bottom > top && x >= sheet.X + StrokeWidth / 2 && x <= sheet.Right - StrokeWidth / 2)
                    lines.Add(new(Point(x, top), Point(x, bottom)));
            }
        }

        if (settings.ShowCellBorders)
        {
            // 边框属于输出版式，预览、矢量 PDF 和打印都绘制同一组线段。
            // 描边向容器内收半个线宽，外边距为 0 时仍完整落在纸内。
            var lines = new List<PdfLayOutLine>(cells.Count * 4);
            foreach (PdfLayOutPlacement cell in cells)
            {
                PdfLayOutRect bounds = cell.CellBounds;
                double stroke = Math.Min(StrokeWidth, Math.Min(bounds.Width, bounds.Height));
                double left = bounds.X + stroke / 2, right = bounds.Right - stroke / 2;
                double top = bounds.Y + stroke / 2, bottom = bounds.Bottom - stroke / 2;
                PdfLayOutLine[] border =
                [
                    new(new(left, top), new(right, top)),
                    new(new(right, top), new(right, bottom)),
                    new(new(right, bottom), new(left, bottom)),
                    new(new(left, bottom), new(left, top)),
                ];
                // 极小容器缩小线宽，防止描边越界。
                if (stroke < StrokeWidth)
                    marks.Add(new(PdfLayOutMarkKind.CellBorder, stroke, Array.AsReadOnly(border)));
                else
                    lines.AddRange(border);
            }
            if (lines.Count > 0)
                marks.Add(new(PdfLayOutMarkKind.CellBorder, StrokeWidth, lines.AsReadOnly()));
        }
        return marks.AsReadOnly();
    }

    // 消除重复单元格边界的浮点噪声，精度为 0.000001 mm。
    private static PdfLayOutPoint Point(double x, double y) => new(Math.Round(x, 6), Math.Round(y, 6));
}
