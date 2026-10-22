using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>把毫米版式写入流；源页面作为 Form XObject 复用，保留矢量内容。</summary>
public sealed class PdfSharpLayOutPdfGenerator : IPdfLayOutPdfGenerator
{
    public Task GenerateAsync(PdfLayOutJob job, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite || !output.CanSeek)
            throw new ArgumentException("PDF 输出流必须可写且可定位。", nameof(output));
        // PDFsharp 的文档和导入缓存只在同一工作线程中使用。
        return Task.Run(() => Generate(job, output, cancellationToken), cancellationToken);
    }

    private static void Generate(PdfLayOutJob job, Stream outputStream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 一次打开源文件，避免导出期间被改写；预览仍可读取它。
        using var input = new FileStream(job.Document.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using XPdfForm source = XPdfForm.FromStream(input);
        if (source.PageCount != job.Document.PageCount)
            throw new InvalidDataException("源 PDF 页数已发生变化，请重新加载文件。");

        using var output = new PdfDocument();
        output.Version = 17;
        output.Info.Title = Path.GetFileNameWithoutExtension(job.Document.FileName) + "_拼版";
        output.Info.Creator = "BigDvToolBox";
        foreach (PdfLayOutSheetLayout sheet in job.Sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.PageIndex = sheet.SourcePageIndex;
            PdfLayOutSourcePageNormalizer.Normalize(source.Page!, job.Document.Pages[sheet.SourcePageIndex]);
            PdfPage page = output.AddPage();
            page.Width = XUnit.FromMillimeter(sheet.SheetSize.Width);
            page.Height = XUnit.FromMillimeter(sheet.SheetSize.Height);
            using XGraphics graphics = XGraphics.FromPdfPage(page);
            graphics.IntersectClip(ToPoints(sheet.SheetBounds));
            foreach (PdfLayOutPlacement cell in sheet.Placements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cell.ClipBounds.IsEmpty) continue;
                XGraphicsState state = graphics.Save();
                graphics.IntersectClip(ToPoints(cell.ClipBounds));
                graphics.DrawImage(source, ToPoints(cell.ImageBounds));
                graphics.Restore(state);
            }
            foreach (PdfLayOutPrintMark mark in sheet.Marks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pen = new XPen(XColors.Black, ToPoints(mark.StrokeWidth));
                foreach (PdfLayOutLine line in mark.Lines)
                    graphics.DrawLine(pen, ToPoints(line.Start.X), ToPoints(line.Start.Y), ToPoints(line.End.X), ToPoints(line.End.Y));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        output.Save(outputStream, closeStream: false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static double ToPoints(double millimeters) => millimeters * 72 / 25.4;
    private static XRect ToPoints(PdfLayOutRect rectangle) => new(
        ToPoints(rectangle.X), ToPoints(rectangle.Y), ToPoints(rectangle.Width), ToPoints(rectangle.Height));
}

