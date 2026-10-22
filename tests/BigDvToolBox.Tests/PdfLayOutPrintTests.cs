using BigDvToolBox_App.Features.PdfLayOut;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutPrintTests
{
    [TestMethod]
    public void PrintVisualGeometry_UsesPhysicalSheetSizeWithoutFittingToDifferentPaper()
    {
        const double mmToDip = 96d / 25.4;
        var a4 = PdfLayOutPrintGeometry.CreateVisualPlan(new(210, 297), new(210 * mmToDip, 297 * mmToDip));
        Assert.AreEqual(0d, a4.Sheet.X, 0.0001);
        Assert.AreEqual(0d, a4.Sheet.Y, 0.0001);
        var letter = PdfLayOutPrintGeometry.CreateVisualPlan(new(210, 297), new(816, 1056));
        Assert.AreEqual(210 * mmToDip, letter.Sheet.Width, 0.0001);
        Assert.AreEqual(297 * mmToDip, letter.Sheet.Height, 0.0001);
        Assert.AreEqual((1056 - 297 * mmToDip) / 2, letter.Sheet.Y, 0.0001);
        Assert.IsTrue(letter.Sheet.Y < 0, "不同纸张应按原尺寸居中裁切，不能自动缩小。");
    }

    [TestMethod]
    public void PrintRasterGeometry_LimitsMemoryAndKeepsSheetAspectRatio()
    {
        PdfLayOutPrintRasterSize raster = PdfLayOutPrintGeometry.CreateRasterSize(new(320, 450), 1200);
        Assert.IsTrue((long)raster.Width * raster.Height <= PdfLayOutPrintGeometry.MaximumRasterPixels);
        Assert.IsTrue(Math.Max(raster.Width, raster.Height) <= PdfLayOutPrintGeometry.MaximumRasterEdge);
        Assert.AreEqual(320d / 450, raster.Width / (double)raster.Height, 0.001);
        Assert.ThrowsExactly<InvalidOperationException>(() => PdfLayOutPrintGeometry.CreateRasterSize(new(210, 297), 0));
    }

    [TestMethod]
    public void PrintPageRanges_AreValidatedAndDoNotDuplicateOverlappingPages()
    {
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, PdfLayOutPrintGeometry.SelectPages(4, null).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 4 }, PdfLayOutPrintGeometry.SelectPages(5, [new(5, 5), new(2, 3), new(1, 2)]).ToArray());
        foreach (PdfLayOutPrintPageRange range in new PdfLayOutPrintPageRange[] { new(0, 1), new(3, 2), new(1, 6) })
            Assert.ThrowsExactly<InvalidOperationException>(() => PdfLayOutPrintGeometry.SelectPages(5, [range]));
        Assert.ThrowsExactly<InvalidOperationException>(() => PdfLayOutPrintGeometry.SelectPages(5, []));
    }

    [TestMethod]
    public async Task PrintService_GeneratesVectorPdfInMemoryAndDisposesItAfterSubmission()
    {
        using var fixture = new PrintFixture();
        PdfLayOutDocument document = await new WindowsPdfLayOutDocumentService(128).LoadAsync(fixture.Source, CancellationToken.None);
        var job = new PdfLayOutJob(document, fixture.Settings);
        Stream? printStream = null;
        var backend = new TestBackend(async (stream, captured, token) =>
        {
            printStream = stream;
            Assert.IsInstanceOfType<MemoryStream>(stream);
            Assert.AreEqual(0L, stream.Position);
            Assert.AreSame(job, captured);
            using (PdfDocument pdf = PdfReader.Open(stream, PdfDocumentOpenMode.Import))
            {
                Assert.AreEqual(2, pdf.PageCount);
                Assert.AreEqual(100d, pdf.Pages[0].Width.Millimeter, 0.001);
            }
            using WindowsPdfLayOutPrintRasterizer renderer = await WindowsPdfLayOutPrintRasterizer.OpenAsync(stream, token);
            PdfLayOutPrintBitmap bitmap = await renderer.RenderAsync(0, new(400, 600), token);
            Assert.AreEqual(400 * 600 * 4, bitmap.BgraPixels.Length);
            Assert.IsTrue(bitmap.BgraPixels.Where((_, index) => index % 4 == 2).Any(value => value > 200));
            return PdfLayOutPrintResult.Submitted;
        });
        var service = new WindowsPdfLayOutPrintService(new PdfSharpLayOutPdfGenerator(), backend);
        Assert.AreEqual(PdfLayOutPrintResult.Submitted, await service.PrintAsync(job, CancellationToken.None));
        Assert.IsNotNull(printStream);
        Assert.IsFalse(printStream.CanRead);
        using var exclusive = new FileStream(fixture.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [TestMethod]
    public async Task PrintService_DisposesMemoryOnDialogCancellationAndBackendFailure()
    {
        using var fixture = new PrintFixture();
        PdfLayOutDocument input = await new WindowsPdfLayOutDocumentService(128).LoadAsync(fixture.Source, CancellationToken.None);
        var job = new PdfLayOutJob(input, fixture.Settings);
        foreach (bool fail in new[] { false, true })
        {
            Stream? printStream = null;
            var backend = new TestBackend((stream, _, _) =>
            {
                printStream = stream;
                Assert.IsTrue(stream.Length > 0);
                return fail ? Task.FromException<PdfLayOutPrintResult>(new IOException("printer failed")) : Task.FromResult(PdfLayOutPrintResult.Cancelled);
            });
            var service = new WindowsPdfLayOutPrintService(new PdfSharpLayOutPdfGenerator(), backend);
            if (fail) await Assert.ThrowsExactlyAsync<IOException>(() => service.PrintAsync(job, CancellationToken.None));
            else Assert.AreEqual(PdfLayOutPrintResult.Cancelled, await service.PrintAsync(job, CancellationToken.None));
            Assert.IsNotNull(printStream);
            Assert.IsFalse(printStream.CanRead);
        }
    }

    [TestMethod]
    public async Task ViewModel_CancelsPrintPreparationAndReleasesBusyStateAndMemory()
    {
        using var fixture = new PrintFixture();
        var exporter = new CancellableExporter();
        var backend = new TestBackend((_, _, _) => throw new AssertFailedException("取消准备后不应打开打印窗口。"));
        var printer = new WindowsPdfLayOutPrintService(exporter, backend);
        var model = new PdfLayOutViewModel(new WindowsPdfLayOutDocumentService(128), null, printer)
        {
            IsAutomaticGrid = false, Columns = 1, Rows = 1, ScaleModeIndex = (int)PdfLayOutScaleMode.Fit,
        };
        await model.LoadDocumentCommand.ExecuteAsync(fixture.Source);
        Task printing = model.PrintCommand.ExecuteAsync(null);
        await exporter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(model.IsBusy);
        Assert.IsFalse(model.CanPrint);
        Assert.IsFalse(model.CanLoadDocument);
        model.CancelOperationCommand.Execute(null);
        await printing;
        Assert.IsFalse(model.IsBusy);
        Assert.IsTrue(model.CanPrint);
        Assert.IsNull(model.ErrorMessage);
        Assert.IsNull(model.LastExportPath);
        StringAssert.Contains(model.StatusMessage, "取消");
        Assert.IsNotNull(exporter.Output);
        Assert.IsFalse(exporter.Output.CanWrite);
    }

    [TestMethod]
    public async Task PrintRasterizer_RendersTopDownColorsAndSupportsCancellation()
    {
        using var fixture = new PrintFixture();
        using (var input = File.OpenRead(fixture.Source))
        {
            using (WindowsPdfLayOutPrintRasterizer renderer = await WindowsPdfLayOutPrintRasterizer.OpenAsync(input, CancellationToken.None))
            {
                PdfLayOutPrintBitmap first = await renderer.RenderAsync(0, new(320, 480), CancellationToken.None);
                Assert.AreEqual(320, first.Width);
                Assert.AreEqual(480, first.Height);
                AssertPixel(first, 80, 120, 0, 0, 255); // 顶部红色
                AssertPixel(first, 80, 360, 255, 0, 0); // 底部蓝色
                PdfLayOutPrintBitmap second = await renderer.RenderAsync(1, new(320, 480), CancellationToken.None);
                AssertPixel(second, 80, 120, 0, 128, 0);
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => renderer.RenderAsync(0, new(320, 480), cancellation.Token));
            }
            Assert.IsTrue(input.CanRead, "渲染器不应关闭调用方的 PDF 流。");
        }
        using var exclusive = new FileStream(fixture.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static void AssertPixel(PdfLayOutPrintBitmap image, int x, int y, int blue, int green, int red)
    {
        int pixel = (y * image.Width + x) * 4;
        Assert.AreEqual(blue, image.BgraPixels[pixel], 5);
        Assert.AreEqual(green, image.BgraPixels[pixel + 1], 5);
        Assert.AreEqual(red, image.BgraPixels[pixel + 2], 5);
    }

    private sealed class TestBackend(Func<Stream, PdfLayOutJob, CancellationToken, Task<PdfLayOutPrintResult>> print) : IPdfLayOutPrintBackend
    {
        public Task<PdfLayOutPrintResult> PrintAsync(Stream pdf, PdfLayOutJob job, CancellationToken token) => print(pdf, job, token);
    }

    private sealed class CancellableExporter : IPdfLayOutPdfGenerator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream? Output { get; private set; }
        public async Task GenerateAsync(PdfLayOutJob job, Stream output, CancellationToken token)
        {
            Output = output;
            await output.WriteAsync(new byte[] { 1, 2, 3 }, token);
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
    }

    private sealed class PrintFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "BigDvToolBox-PrintTest-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(_directory, "原稿.pdf");
        public PdfLayOutSettings Settings => new()
        {
            SheetSize = new(100, 150), GridMode = PdfLayOutGridMode.Manual, Columns = 1, Rows = 1,
            ScaleMode = PdfLayOutScaleMode.Fit, Marks = new() { ShowCropMarks = false },
        };
        public PrintFixture()
        {
            Directory.CreateDirectory(_directory);
            using var pdf = new PdfDocument();
            foreach (bool first in new[] { true, false })
            {
                PdfPage page = pdf.AddPage();
                page.Width = XUnit.FromMillimeter(100);
                page.Height = XUnit.FromMillimeter(150);
                using XGraphics graphics = XGraphics.FromPdfPage(page, XGraphicsUnit.Millimeter);
                graphics.DrawRectangle(first ? XBrushes.Red : XBrushes.Green, 0, 0, 100, 75);
                graphics.DrawRectangle(first ? XBrushes.Blue : XBrushes.Yellow, 0, 75, 100, 75);
            }
            pdf.Save(Source);
        }
        public void Dispose() { File.Delete(Source); Directory.Delete(_directory); }
    }
}
