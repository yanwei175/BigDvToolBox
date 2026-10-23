namespace BigDvToolBox_App.Features.PdfLayOut;

public static partial class PdfLayOutCalculator
{
    public static PdfLayOutSheetLayout CalculateGrid(PdfLayOutSettings settings)
    {
        var grid = Calculate(new(0, new(1, 1)), settings with { GridMode = PdfLayOutGridMode.Manual, SourceCrop = default });
        return grid with { Placements = [], CroppedPageSize = default };
    }

    public static PdfLayOutSheetLayout CalculateFree(PdfLayOutSettings settings, IEnumerable<PdfLayOutCellContent> contents)
    {
        PdfLayOutSheetLayout grid = CalculateGrid(settings);
        if (!grid.IsValid) return grid;
        var placements = new List<PdfLayOutPlacement>();
        var occupied = new HashSet<(int, int)>();
        string? firstError = null;
        foreach (PdfLayOutCellContent content in contents)
        {
            string? error = null;
            if (content.Row < 0 || content.Row >= grid.Rows || content.Column < 0 || content.Column >= grid.Columns ||
                !occupied.Add((content.Row, content.Column))) error = "容器位置无效或重复。";
            else if (content.SourcePageIndex < 0 || content.SourcePageIndex >= content.Document.PageCount) error = "源页码无效。";
            else if (!settings.SourceCrop.IsValid) error = "源裁切必须是有效的非负数。";
            else
            {
                var size = content.Document.Pages[content.SourcePageIndex].Size;
                if (size.Width <= settings.SourceCrop.Left + settings.SourceCrop.Right ||
                    size.Height <= settings.SourceCrop.Top + settings.SourceCrop.Bottom) error = "裁切后没有可用区域。";
            }
            if (error is not null)
            {
                firstError ??= $"第 {content.Row + 1} 行第 {content.Column + 1} 列（{content.Document.FileName}）：{error}";
                continue;
            }
            PdfLayOutCell cell = grid.Cells[content.Row * grid.Columns + content.Column];
            placements.Add(CreatePlacement(content.Document.Pages[content.SourcePageIndex], settings, content.Row, content.Column, cell.Bounds)
                with { SourceDocumentId = content.DocumentId });
        }
        return grid with { Placements = placements.AsReadOnly(), ErrorMessage = firstError };
    }

    private static PdfLayOutPlacement CreatePlacement(PdfLayOutSourcePage page, PdfLayOutSettings settings,
        int row, int column, PdfLayOutRect cell)
    {
        var crop = settings.SourceCrop;
        double width = page.Size.Width - crop.Left - crop.Right, height = page.Size.Height - crop.Top - crop.Bottom;
        double scale = settings.ScaleMode switch
        {
            PdfLayOutScaleMode.Fit => Math.Min(cell.Width / width, cell.Height / height),
            PdfLayOutScaleMode.Fill => Math.Max(cell.Width / width, cell.Height / height),
            PdfLayOutScaleMode.Custom => settings.ScalePercent / 100,
            _ => 1,
        };
        var content = new PdfLayOutRect(cell.X + (cell.Width - width * scale) / 2 + settings.Offset.X,
            cell.Y + (cell.Height - height * scale) / 2 + settings.Offset.Y, width * scale, height * scale);
        PdfLayOutRect trim = content.Intersect(cell);
        return new(page.PageIndex, row, column, cell, content, new(crop.Left, crop.Top, width, height))
        {
            Scale = scale,
            ImageBounds = new(content.X - crop.Left * scale, content.Y - crop.Top * scale, page.Size.Width * scale, page.Size.Height * scale),
            TrimBounds = trim,
            ClipBounds = trim.Intersect(new(0, 0, settings.SheetSize.Width, settings.SheetSize.Height)),
        };
    }
}
