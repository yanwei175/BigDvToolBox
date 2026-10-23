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
        var sources = new Dictionary<string, ImportedSource>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in job.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sources.Add(entry.Key, new ImportedSource(entry.Value));
            }
            var normalized = new HashSet<(ImportedSource, int)>();
            using var output = new PdfDocument();
            output.Version = 17;
            output.Info.Title = job.DisplayName;
            output.Info.Creator = "BigDvToolBox";
            foreach (PdfLayOutSheetLayout sheet in job.Sheets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PdfPage page = output.AddPage();
                page.Width = XUnit.FromMillimeter(sheet.SheetSize.Width);
                page.Height = XUnit.FromMillimeter(sheet.SheetSize.Height);
                using XGraphics graphics = XGraphics.FromPdfPage(page);
                graphics.IntersectClip(ToPoints(sheet.SheetBounds));
                foreach (PdfLayOutPlacement cell in sheet.Placements)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (cell.ClipBounds.IsEmpty) continue;
                    ImportedSource imported = sources[cell.SourceDocumentId];
                    XPdfForm source = imported.Form;
                    source.PageIndex = cell.SourcePageIndex;
                    if (normalized.Add((imported, cell.SourcePageIndex)))
                        PdfLayOutSourcePageNormalizer.Normalize(source.Page!, imported.Document.Pages[cell.SourcePageIndex]);
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
        finally { foreach (ImportedSource source in sources.Values) source.Dispose(); }
    }

    private sealed class ImportedSource : IDisposable
    {
        private readonly FileStream _input;
        public PdfLayOutDocument Document { get; }
        public XPdfForm Form { get; }

        public ImportedSource(PdfLayOutDocument document)
        {
            Document = document;
            _input = new FileStream(document.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                Form = XPdfForm.FromStream(_input);
                if (Form.PageCount != document.PageCount)
                {
                    Form.Dispose();
                    throw new InvalidDataException($"源 PDF 页数已变化：{document.FileName}，请重新加载。");
                }
            }
            catch { _input.Dispose(); throw; }
        }
        public void Dispose() { Form.Dispose(); _input.Dispose(); }
    }
    private static double ToPoints(double millimeters) => millimeters * 72 / 25.4;
    private static XRect ToPoints(PdfLayOutRect rectangle) => new(
        ToPoints(rectangle.X), ToPoints(rectangle.Y), ToPoints(rectangle.Width), ToPoints(rectangle.Height));
}

