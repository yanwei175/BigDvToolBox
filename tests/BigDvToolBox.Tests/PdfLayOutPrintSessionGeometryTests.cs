using BigDvToolBox_App.Features.PdfLayOut;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System.Text.Json;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutPrintSessionGeometryTests
{
    private const double Dip = 96d / 25.4;
    private static readonly PdfLayOutSize A4 = new(210 * Dip, 297 * Dip);
    private static readonly PdfLayOutRect A4Printable = new(3 * Dip, 3 * Dip, 204 * Dip, 291 * Dip);

    [TestMethod]
    [DataRow(0d)]
    [DataRow(2d)]
    [DataRow(10d)]
    public void DesignMargin_IsMeasuredFromPaperEdge_AndHardwareMarginOnlyClips(double margin)
    {
        var job = Job("source.pdf", margin);
        var session = new PdfLayOutPrintSessionGeometry(job.Sheets.Select(sheet => sheet.SheetSize));
        Assert.IsTrue(session.TryCreatePage(session.Revision, 0, A4, A4Printable, out var plan));
        var cell = job.Sheets[0].Cells[0].Bounds;
        var point = plan.SheetMillimetersToPaperDip(new(cell.X, cell.Y));
        Assert.AreEqual(margin, point.X / Dip, 0.0001, "硬件左边距不能叠加到版式边距。");
        Assert.AreEqual(margin, point.Y / Dip, 0.0001);
        Assert.AreEqual(3d, plan.ContentClip.X / Dip, 0.0001);
        Assert.AreEqual(210d, plan.Sheet.Width / Dip, 0.0001);
        Assert.AreEqual(297d, plan.Sheet.Height / Dip, 0.0001);
        Assert.AreEqual(margin, job.Settings.SheetMargins.Left);
    }

    [TestMethod]
    public void PrinterAndPaperChanges_RecalculateBoundary_AndDiscardOldDriverResults()
    {
        var session = new PdfLayOutPrintSessionGeometry([new(210, 297)]);
        long first = session.Revision;
        Assert.IsTrue(session.TryCreatePage(first, 0, A4, A4Printable, out var bordered));
        session.Invalidate(); // 切到无边距打印机或纸张模式。
        Assert.IsFalse(session.TryCreatePage(first, 0, A4, A4Printable, out var stale));
        Assert.IsNull(stale);
        Assert.IsTrue(session.TryCreatePage(session.Revision, 0, A4, new(0, 0, A4.Width, A4.Height), out var borderless));
        Assert.IsFalse(borderless.HasHardwareMargins);
        Assert.AreEqual(bordered.Sheet, borderless.Sheet);
        session.Invalidate(); // 同一纸张换到四边不对称的驱动设置。
        var asymmetric = new PdfLayOutRect(5 * Dip, 7 * Dip, 200 * Dip, 280 * Dip);
        Assert.IsTrue(session.TryCreatePage(session.Revision, 0, A4, asymmetric, out var changed));
        Assert.AreEqual(asymmetric, changed.PrintableArea);
        Assert.AreEqual(bordered.Sheet, changed.Sheet);
        session.Invalidate(); // 更换纸张：仍按原始印张尺寸居中裁切，不适应纸张缩小。
        var a5 = new PdfLayOutSize(148 * Dip, 210 * Dip);
        Assert.IsTrue(session.TryCreatePage(session.Revision, 0, a5, new(0, 0, a5.Width, a5.Height), out var smaller));
        Assert.AreEqual(210d, smaller.Sheet.Width / Dip, 0.0001);
        Assert.AreEqual(297d, smaller.Sheet.Height / Dip, 0.0001);
        Assert.AreEqual(-31d, smaller.Sheet.X / Dip, 0.0001);
        session.Invalidate();
        var landscape = new PdfLayOutSize(297 * Dip, 210 * Dip);
        Assert.IsTrue(session.TryCreatePage(session.Revision, 0, landscape, new(0, 0, landscape.Width, landscape.Height), out var rotatedPaper));
        Assert.AreEqual(landscape, rotatedPaper.Paper);
        Assert.AreEqual(210d, rotatedPaper.Sheet.Width / Dip, 0.0001);
        Assert.AreEqual(297d, rotatedPaper.Sheet.Height / Dip, 0.0001);
    }

    [TestMethod]
    public void NewPrintSession_DoesNotInheritPreviousPrinterGeometry_AndCopiesOnlySheetSizes()
    {
        PdfLayOutSize[] sizes = [new(210, 297)];
        var first = new PdfLayOutPrintSessionGeometry(sizes);
        sizes[0] = new(148, 210);
        Assert.IsTrue(first.TryCreatePage(first.Revision, 0, A4, A4Printable, out var firstPlan));
        Assert.AreEqual(210d, firstPlan.Sheet.Width / Dip, 0.0001);
        first.Invalidate();
        var second = new PdfLayOutPrintSessionGeometry([new(210, 297)]);
        Assert.IsTrue(second.TryCreatePage(second.Revision, 0, A4, new(0, 0, A4.Width, A4.Height), out var secondPlan));
        Assert.IsFalse(secondPlan.HasHardwareMargins);
        Assert.AreEqual(0L, second.Revision);
    }

    [TestMethod]
    public async Task SwitchingPrinters_DoesNotChangeConfigurationLayoutOrExportedPdf()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BigDvToolBox-PrintGeometry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "source.pdf");
        try
        {
            using (var source = new PdfDocument())
            {
                var page = source.AddPage();
                page.Width = XUnit.FromMillimeter(60); page.Height = XUnit.FromMillimeter(30);
                using (var graphics = XGraphics.FromPdfPage(page)) graphics.DrawRectangle(XBrushes.Black, 0, 0, 100, 50);
                source.Save(path);
            }
            var job = Job(path, 10);
            string configuration = JsonSerializer.Serialize(job.Settings);
            string layout = JsonSerializer.Serialize(job.Sheets);
            byte[] before = await ExportPixels(job);
            var session = new PdfLayOutPrintSessionGeometry(job.Sheets.Select(sheet => sheet.SheetSize));
            foreach (var area in new PdfLayOutRect[] { A4Printable, new(0, 0, A4.Width, A4.Height), new(5 * Dip, 8 * Dip, 200 * Dip, 280 * Dip) })
            {
                session.Invalidate();
                Assert.IsTrue(session.TryCreatePage(session.Revision, 0, A4, area, out _));
            }
            Assert.AreEqual(configuration, JsonSerializer.Serialize(job.Settings));
            Assert.AreEqual(layout, JsonSerializer.Serialize(job.Sheets));
            CollectionAssert.AreEqual(before, await ExportPixels(job), "切换打印设置不能影响导出 PDF 的页面尺寸和内容位置。");
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    private static PdfLayOutJob Job(string path, double margin) => new(
        new PdfLayOutDocument(path, [new(0, new(60, 30))]), new()
        {
            SheetSize = new(210, 297), SheetMargins = new(margin, margin, margin, margin),
            GridMode = PdfLayOutGridMode.Manual, Rows = 2, Columns = 2,
            HorizontalGap = 5, VerticalGap = 5, ScaleMode = PdfLayOutScaleMode.Fit,
            Marks = new() { ShowCropMarks = false, ShowCellBorders = true },
        });

    private static async Task<byte[]> ExportPixels(PdfLayOutJob job)
    {
        using var output = new MemoryStream();
        await new PdfSharpLayOutPdfGenerator().GenerateAsync(job, output, CancellationToken.None);
        output.Position = 0;
        using (var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            // PDFsharp 的页面点数序列化存在小数舍入，允许 0.001 mm 的数值误差。
            Assert.AreEqual(210d, pdf.Pages[0].Width.Millimeter, 0.001);
            Assert.AreEqual(297d, pdf.Pages[0].Height.Millimeter, 0.001);
        }
        using var renderer = await WindowsPdfLayOutPrintRasterizer.OpenAsync(output, CancellationToken.None);
        return (await renderer.RenderAsync(0, new(420, 594), CancellationToken.None)).BgraPixels;
    }
}
