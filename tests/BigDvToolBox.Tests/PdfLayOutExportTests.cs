using BigDvToolBox_App.Features.PdfLayOut;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutExportTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Generator_WritesVectorPdfToMemoryAndLeavesCallerStreamOpen()
    {
        using var fixture = new ExportFixture();
        PdfLayOutDocument input = await new WindowsPdfLayOutDocumentService(128).LoadAsync(fixture.Source, CancellationToken.None);
        using var memory = new MemoryStream();
        await new PdfSharpLayOutPdfGenerator().GenerateAsync(new(input, Settings()), memory, CancellationToken.None);
        Assert.IsTrue(memory.CanRead && memory.CanWrite);
        Assert.IsTrue(memory.Length > 0);
        memory.Position = 0;
        using PdfDocument output = PdfReader.Open(memory, PdfDocumentOpenMode.Import);
        Assert.AreEqual(input.PageCount, output.PageCount);
        Assert.AreEqual(200d, output.Pages[0].Width.Millimeter, 0.001);
        PdfDictionary[] objects = output.Internals.GetAllObjects().OfType<PdfDictionary>().ToArray();
        Assert.IsTrue(objects.Any(item => item.Elements.GetName("/Subtype") == "/Form"));
        Assert.IsFalse(objects.Any(item => item.Elements.GetName("/Subtype") == "/Image"));
        Assert.IsFalse(File.Exists(fixture.Output), "生成到内存不应写入导出文件。");
    }

    [TestMethod]
    [DataRow(PdfLayOutScaleMode.OriginalSize)]
    [DataRow(PdfLayOutScaleMode.Fit)]
    [DataRow(PdfLayOutScaleMode.Fill)]
    [DataRow(PdfLayOutScaleMode.Custom)]
    public async Task Export_PreservesVectorsAndMatchesPreviewGeometry(PdfLayOutScaleMode mode)
    {
        using var fixture = new ExportFixture();
        var renderer = new WindowsPdfLayOutDocumentService(720);
        PdfLayOutDocument input = await renderer.LoadAsync(fixture.Source, CancellationToken.None);
        Assert.AreEqual(110d, input.Pages[6].Size.Width, 0.001, "UserUnit=2 的旋转裁切页宽应为 110 mm。");
        Assert.AreEqual(160d, input.Pages[6].Size.Height, 0.001);
        var job = new PdfLayOutJob(input, Settings() with { ScaleMode = mode });
        byte[] original = await File.ReadAllBytesAsync(fixture.Source);
        await new PdfSharpLayOutExportService().ExportAsync(job, fixture.Output, CancellationToken.None);

        using (PdfDocument pdf = PdfReader.Open(fixture.Output, PdfDocumentOpenMode.Import))
        {
            Assert.AreEqual(input.PageCount, pdf.PageCount);
            Assert.IsTrue(pdf.Pages.Cast<PdfPage>().All(page => Math.Abs(page.Width.Millimeter - 200) < 0.001 && Math.Abs(page.Height.Millimeter - 220) < 0.001));
            PdfDictionary[] objects = pdf.Internals.GetAllObjects().OfType<PdfDictionary>().ToArray();
            Assert.AreEqual(0, objects.Count(item => item.Elements.GetName("/Subtype") == "/Image"), "纯矢量源页不能变成位图。");
            Assert.AreEqual(input.PageCount, objects.Count(item => item.Elements.GetName("/Subtype") == "/Form"), "同页的多个单元格应复用同一个 Form。");
            Assert.IsTrue(objects.Any(item => item.Elements.GetName("/BaseFont") == "/Helvetica"), "应保留源字体资源。");
            Assert.IsTrue(objects.Any(item => item.Stream is not null && Encoding.ASCII.GetString(item.Stream.UnfilteredValue).Contains("(VECTOR)")), "应保留源文本绘制指令。");
        }
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(fixture.Source), "规范页面坐标不应改写源文件。");

        PdfLayOutDocument output = await renderer.LoadAsync(fixture.Output, CancellationToken.None);
        for (int index = 0; index < input.PageCount; index++)
        {
            PdfLayOutPagePreview source = await renderer.RenderAsync(input, index, CancellationToken.None);
            PdfLayOutPagePreview exported = await renderer.RenderAsync(output, index, CancellationToken.None);
            Raster sourcePixels = await DecodeAsync(source);
            Raster outputPixels = await DecodeAsync(exported);
            byte[] reference = ComposeReference(job.Sheets[index], sourcePixels, outputPixels.Width, outputPixels.Height);
            double mismatch = MismatchFraction(reference, outputPixels.Pixels);
            if (index == 0 || mismatch >= 0.02)
                await SaveDiagnosticAsync($"{mode}-page-{index + 1}.png", exported.PngBytes);
            Assert.IsTrue(mismatch < 0.02, $"{mode} 第 {index + 1} 页与原生预览按统一版式合成的结果不一致：{mismatch:P2}。");
        }
        string sample = Path.Combine(TestContext.TestRunResultsDirectory!, $"export-{mode}.pdf");
        File.Copy(fixture.Output, sample, overwrite: true);
        TestContext.AddResultFile(sample);
    }

    [TestMethod]
    public async Task Export_DrawsCropAndContainerBordersFromResolvedGeometry()
    {
        using var fixture = new ExportFixture();
        var renderer = new WindowsPdfLayOutDocumentService(1600);
        PdfLayOutDocument input = await renderer.LoadAsync(fixture.Source, CancellationToken.None);
        var job = new PdfLayOutJob(input, Settings() with
        {
            Columns = 1, Rows = 1, SourceCrop = default, ScaleMode = PdfLayOutScaleMode.OriginalSize,
            Marks = new() { Length = 4, Offset = 2, ShowCellBorders = true },
        });
        await new PdfSharpLayOutExportService().ExportAsync(job, fixture.Output, CancellationToken.None);
        PdfLayOutDocument output = await renderer.LoadAsync(fixture.Output, CancellationToken.None);
        PdfLayOutPagePreview preview = await renderer.RenderAsync(output, 0, CancellationToken.None);
        Raster pixels = await DecodeAsync(preview);

        Assert.IsTrue(job.Sheets[0].Marks.Any(mark => mark.Kind == PdfLayOutMarkKind.Crop));
        Assert.AreEqual(4, job.Sheets[0].Marks.Single(mark => mark.Kind == PdfLayOutMarkKind.CellBorder).Lines.Count);
        foreach (PdfLayOutPrintMark mark in job.Sheets[0].Marks)
        {
            foreach (PdfLayOutLine line in mark.Lines)
                AssertInkNear(pixels, job.Sheets[0].SheetSize, new((line.Start.X + line.End.X) / 2, (line.Start.Y + line.End.Y) / 2));
        }
        using PdfDocument pdf = PdfReader.Open(fixture.Output, PdfDocumentOpenMode.Import);
        string pageContent = Encoding.ASCII.GetString(pdf.Pages[0].Contents.CreateSingleContent().Stream.Value);
        StringAssert.Contains(pageContent, " l", "裁切标记和边框应导出为矢量线段。");
        await SaveDiagnosticAsync("print-marks.png", preview.PngBytes);
        string sample = Path.Combine(TestContext.TestRunResultsDirectory!, "export-print-marks.pdf");
        File.Copy(fixture.Output, sample, overwrite: true);
        TestContext.AddResultFile(sample);
    }

    [TestMethod]
    public async Task Export_RejectsSourcePathAndChangedSourceMetadata()
    {
        using var fixture = new ExportFixture();
        PdfLayOutDocument input = await new WindowsPdfLayOutDocumentService().LoadAsync(fixture.Source, CancellationToken.None);
        var service = new PdfSharpLayOutExportService();
        byte[] original = await File.ReadAllBytesAsync(fixture.Source);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExportAsync(new(input, Settings()), fixture.Source, CancellationToken.None));
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(fixture.Source));

        var wrongCount = new PdfLayOutDocument(fixture.Source, [input.Pages[0]]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ExportAsync(new(wrongCount, Settings()), fixture.Output, CancellationToken.None));
        var wrongSizes = new PdfLayOutDocument(fixture.Source, input.Pages.Select(page => page with { Size = new(100, 100) }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ExportAsync(new(wrongSizes, Settings()), fixture.Output, CancellationToken.None));
        Assert.IsFalse(File.Exists(fixture.Output));
        Assert.AreEqual(0, Directory.GetFiles(fixture.DirectoryPath, ".imposer-*.tmp").Length);
    }

    [TestMethod]
    public async Task Export_CancellationAndCommitFailurePreserveExistingTarget()
    {
        using var fixture = new ExportFixture();
        PdfLayOutDocument input = await new WindowsPdfLayOutDocumentService().LoadAsync(fixture.Source, CancellationToken.None);
        var job = new PdfLayOutJob(input, Settings());
        var service = new PdfSharpLayOutExportService();
        byte[] sentinel = Encoding.ASCII.GetBytes("existing file");
        await File.WriteAllBytesAsync(fixture.Output, sentinel);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ExportAsync(job, fixture.Output, cancellation.Token));
        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(fixture.Output));

        // 允许读取但禁止替换，以确定性地覆盖“已生成临时 PDF，提交目标失败”的路径。
        using (var locked = new FileStream(fixture.Output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception failure = await Assert.ThrowsAsync<Exception>(() => service.ExportAsync(job, fixture.Output, CancellationToken.None));
            Assert.IsTrue(failure is IOException or UnauthorizedAccessException);
        }
        CollectionAssert.AreEqual(sentinel, await File.ReadAllBytesAsync(fixture.Output));
        Assert.AreEqual(0, Directory.GetFiles(fixture.DirectoryPath, ".imposer-*.tmp").Length);
        await service.ExportAsync(job, fixture.Output, CancellationToken.None);
        using (PdfDocument replaced = PdfReader.Open(fixture.Output, PdfDocumentOpenMode.Import))
            Assert.AreEqual(input.PageCount, replaced.PageCount);
        // 导出失败后源文件也应能独占打开。
        using var exclusive = new FileStream(fixture.Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [TestMethod]
    public async Task ViewModel_ExportsWithConcreteServiceAndReportsSavedPath()
    {
        using var fixture = new ExportFixture();
        var model = new PdfLayOutViewModel(new WindowsPdfLayOutDocumentService(128), new PdfSharpLayOutExportService(), null)
        {
            IsAutomaticGrid = false, Columns = 2, Rows = 2, ScaleModeIndex = (int)PdfLayOutScaleMode.Fit,
            ShowPageContent = false,
        };
        Assert.IsFalse(model.CanExport);
        await model.LoadDocumentCommand.ExecuteAsync(fixture.Source);
        Assert.IsTrue(model.CanExport);
        await model.ExportPdfCommand.ExecuteAsync(fixture.Output);
        Assert.IsNull(model.ErrorMessage);
        Assert.IsFalse(model.IsBusy);
        Assert.AreEqual(fixture.Output, model.LastExportPath);
        StringAssert.Contains(model.StatusMessage, fixture.Output);
        using PdfDocument pdf = PdfReader.Open(fixture.Output, PdfDocumentOpenMode.Import);
        Assert.AreEqual(model.SourceDocument!.PageCount, pdf.PageCount);
        Assert.IsTrue(pdf.Internals.GetAllObjects().OfType<PdfDictionary>().Any(item => item.Elements.GetName("/Subtype") == "/Form"), "隐藏预览内容不应隐藏导出的页面。");
    }

    private static PdfLayOutSettings Settings() => new()
    {
        SheetSize = new(200, 220), SheetMargins = new(20, 20, 20, 20),
        GridMode = PdfLayOutGridMode.Manual, Columns = 2, Rows = 2,
        HorizontalGap = 6, VerticalGap = 8, SourceCrop = new(5, 3, 7, 4), Offset = new(3, -2),
        ScaleMode = PdfLayOutScaleMode.Fit, ScalePercent = 135,
        Marks = new() { ShowCropMarks = false },
    };

    private async Task SaveDiagnosticAsync(string name, byte[] png)
    {
        string path = Path.Combine(TestContext.TestRunResultsDirectory!, name);
        await File.WriteAllBytesAsync(path, png);
        TestContext.AddResultFile(path);
    }

    private static async Task<Raster> DecodeAsync(PdfLayOutPagePreview preview)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(preview.PngBytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return new(checked((int)decoder.PixelWidth), checked((int)decoder.PixelHeight), pixels.DetachPixelData());
    }

    // 用原生源页预览及共享毫米矩形合成参考图，与 PDFsharp 导出后重新渲染的图像交叉验证。
    private static byte[] ComposeReference(PdfLayOutSheetLayout layout, Raster source, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        foreach (PdfLayOutPlacement cell in layout.Placements)
        {
            int left = Math.Max(0, (int)Math.Floor(cell.ClipBounds.X / layout.SheetSize.Width * width));
            int top = Math.Max(0, (int)Math.Floor(cell.ClipBounds.Y / layout.SheetSize.Height * height));
            int right = Math.Min(width, (int)Math.Ceiling(cell.ClipBounds.Right / layout.SheetSize.Width * width));
            int bottom = Math.Min(height, (int)Math.Ceiling(cell.ClipBounds.Bottom / layout.SheetSize.Height * height));
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    double mmX = (x + 0.5) / width * layout.SheetSize.Width;
                    double mmY = (y + 0.5) / height * layout.SheetSize.Height;
                    if (mmX < cell.ClipBounds.X || mmX >= cell.ClipBounds.Right || mmY < cell.ClipBounds.Y || mmY >= cell.ClipBounds.Bottom) continue;
                    int sourceX = Math.Clamp((int)((mmX - cell.ImageBounds.X) / cell.ImageBounds.Width * source.Width), 0, source.Width - 1);
                    int sourceY = Math.Clamp((int)((mmY - cell.ImageBounds.Y) / cell.ImageBounds.Height * source.Height), 0, source.Height - 1);
                    Array.Copy(source.Pixels, (sourceY * source.Width + sourceX) * 4, pixels, (y * width + x) * 4, 4);
                }
        }
        return pixels;
    }

    private static double MismatchFraction(byte[] expected, byte[] actual)
    {
        int different = 0;
        for (int pixel = 0; pixel < actual.Length; pixel += 4)
            if (Math.Abs(expected[pixel] - actual[pixel]) > 60 ||
                Math.Abs(expected[pixel + 1] - actual[pixel + 1]) > 60 ||
                Math.Abs(expected[pixel + 2] - actual[pixel + 2]) > 60) different++;
        return different / (actual.Length / 4d);
    }

    private static void AssertInkNear(Raster raster, PdfLayOutSize size, PdfLayOutPoint point)
    {
        int x = (int)Math.Round(point.X / size.Width * raster.Width), y = (int)Math.Round(point.Y / size.Height * raster.Height);
        bool found = false;
        for (int offsetY = -1; offsetY <= 1; offsetY++)
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                int pixel = (Math.Clamp(y + offsetY, 0, raster.Height - 1) * raster.Width + Math.Clamp(x + offsetX, 0, raster.Width - 1)) * 4;
                found |= raster.Pixels[pixel] < 230 && raster.Pixels[pixel + 1] < 230 && raster.Pixels[pixel + 2] < 230;
            }
        Assert.IsTrue(found, $"毫米坐标 {point} 缺少印刷标记。");
    }

    private sealed record Raster(int Width, int Height, byte[] Pixels);

    private sealed class ExportFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "BigDvToolBox-Export-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(DirectoryPath, "中文 原稿.pdf");
        public string Output => Path.Combine(DirectoryPath, "中文 拼版.pdf");

        public ExportFixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            using var pdf = new PdfDocument();
            pdf.Version = 17;
            AddPage(pdf, 0, cropped: false);
            foreach (int rotation in new[] { 0, 90, 180, 270 }) AddPage(pdf, rotation, cropped: true);
            AddPage(pdf, 270, cropped: true, shifted: true);
            AddPage(pdf, 90, cropped: true, userUnit: 2);
            pdf.Save(Source);
        }

        private static void AddPage(PdfDocument pdf, int rotation, bool cropped, bool shifted = false, double userUnit = 1)
        {
            PdfPage page = pdf.AddPage();
            page.Width = XUnit.FromMillimeter(100);
            page.Height = XUnit.FromMillimeter(80);
            using (XGraphics graphics = XGraphics.FromPdfPage(page, XGraphicsUnit.Millimeter))
            {
                graphics.DrawRectangle(XBrushes.Red, 0, 0, 50, 40);
                graphics.DrawRectangle(XBrushes.Green, 50, 0, 50, 40);
                graphics.DrawRectangle(XBrushes.Blue, 0, 40, 50, 40);
                graphics.DrawRectangle(XBrushes.Yellow, 50, 40, 50, 40);
            }
            // 直接添加标准 PDF 字体和文本，避免测试依赖本机字体解析器。
            var font = new PdfDictionary(pdf);
            font.Elements.SetName("/Type", "/Font");
            font.Elements.SetName("/Subtype", "/Type1");
            font.Elements.SetName("/BaseFont", "/Helvetica");
            pdf.Internals.AddObject(font);
            var fonts = new PdfDictionary(pdf);
            fonts.Elements["/FTest"] = font.Reference!;
            PdfDictionary resources = page.Elements.GetDictionary("/Resources") ?? new PdfDictionary(pdf);
            resources.Elements["/Font"] = fonts;
            page.Elements["/Resources"] = resources;
            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("q 0 0 0 rg BT /FTest 14 Tf 1 0 0 1 60 130 Tm (VECTOR) Tj ET Q"));

            double dx = shifted ? 20 : 0, dy = shifted ? -10 : 0;
            if (shifted)
            {
                page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes("q 1 0 0 1 20 -10 cm\n"));
                page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("\nQ"));
                page.MediaBox = new PdfRectangle(new XPoint(dx, dy), new XPoint(dx + page.Width.Point, dy + page.Height.Point));
            }
            if (cropped)
                page.CropBox = new PdfRectangle(new XPoint(dx + XUnit.FromMillimeter(10).Point, dy + XUnit.FromMillimeter(15).Point),
                    new XPoint(dx + XUnit.FromMillimeter(90).Point, dy + XUnit.FromMillimeter(70).Point));
            page.Rotate = rotation;
            if (userUnit != 1) page.Elements.SetReal("/UserUnit", userUnit);
        }

        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(DirectoryPath)) File.Delete(file);
            Directory.Delete(DirectoryPath);
        }
    }
}
