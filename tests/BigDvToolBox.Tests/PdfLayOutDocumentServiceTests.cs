using BigDvToolBox_App.Features.PdfLayOut;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutDocumentServiceTests
{
    [TestMethod]
    public async Task Load_ReportsActualPageCountSizesRotationAndCropBox()
    {
        using var fixture = new PdfFixture();
        var service = new WindowsPdfLayOutDocumentService();
        PdfLayOutDocument document = await service.LoadAsync(fixture.Path, CancellationToken.None);

        Assert.AreEqual(3, document.PageCount);
        Assert.AreEqual(210d, document.Pages[0].Size.Width, 0.001);
        Assert.AreEqual(297d, document.Pages[0].Size.Height, 0.001);
        Assert.AreEqual(200d, document.Pages[1].Size.Width, 0.001);
        Assert.AreEqual(100d, document.Pages[1].Size.Height, 0.001);
        Assert.AreEqual(80d, document.Pages[2].Size.Width, 0.001);
        Assert.AreEqual(160d, document.Pages[2].Size.Height, 0.001);
        Assert.IsTrue(PdfLayOutCalculator.Calculate(document.Pages[0], new()).IsValid);
    }

    [TestMethod]
    public async Task Render_UsesSelectedPageAndLimitsImageSize()
    {
        using var fixture = new PdfFixture();
        var service = new WindowsPdfLayOutDocumentService(384);
        PdfLayOutDocument document = await service.LoadAsync(fixture.Path, CancellationToken.None);
        PdfLayOutPagePreview first = await service.RenderAsync(document, 0, CancellationToken.None);
        PdfLayOutPagePreview second = await service.RenderAsync(document, 1, CancellationToken.None);

        Assert.IsTrue(first.PngBytes.Length > 0);
        Assert.AreEqual(384u, Math.Max(first.PixelWidth, first.PixelHeight));
        Assert.AreEqual(1, second.PageIndex);
        Assert.AreEqual(384u, second.PixelWidth);
        Assert.AreEqual(192u, second.PixelHeight);
        byte[] firstCenter = await ReadCenterPixelAsync(first);
        byte[] secondCenter = await ReadCenterPixelAsync(second);
        Assert.IsTrue(firstCenter[2] > 230 && firstCenter[0] < 20, "第一页应为红色。");
        Assert.IsTrue(secondCenter[0] > 230 && secondCenter[2] < 20, "旋转后的第二页应为蓝色。");
    }

    [TestMethod]
    public async Task CorruptPdf_FailsWithoutLeavingSourceFileOpen()
    {
        using var fixture = new PdfFixture();
        await File.WriteAllTextAsync(fixture.Path, "this is not a PDF");
        var service = new WindowsPdfLayOutDocumentService();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.LoadAsync(fixture.Path, CancellationToken.None));
        // 能独占打开说明异常路径也释放了输入流。
        using var exclusive = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [TestMethod]
    public async Task PasswordProtectedPdf_ReportsReadableError()
    {
        using var fixture = new PdfFixture(passwordProtected: true);
        var service = new WindowsPdfLayOutDocumentService();
        InvalidDataException exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => service.LoadAsync(fixture.Path, CancellationToken.None));
        StringAssert.Contains(exception.Message, "密码");
    }

    [TestMethod]
    public async Task CancelledLoad_DoesNotReadFile()
    {
        var service = new WindowsPdfLayOutDocumentService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.LoadAsync("missing.pdf", cancellation.Token));
    }

    [TestMethod]
    public async Task Render_RejectsChangedPageDimensions()
    {
        using var fixture = new PdfFixture();
        var service = new WindowsPdfLayOutDocumentService(128);
        var staleMetadata = new PdfLayOutDocument(fixture.Path, [new(0, new(100, 100))]);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.RenderAsync(staleMetadata, 0, CancellationToken.None));
    }

    private static async Task<byte[]> ReadCenterPixelAsync(PdfLayOutPagePreview preview)
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
        Assert.AreEqual(preview.PixelWidth, decoder.PixelWidth);
        Assert.AreEqual(preview.PixelHeight, decoder.PixelHeight);
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] bytes = pixels.DetachPixelData();
        int center = checked((int)(((decoder.PixelHeight / 2) * decoder.PixelWidth + decoder.PixelWidth / 2) * 4));
        return bytes[center..(center + 4)];
    }

    private sealed class PdfFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BigDvToolBox-PdfLayOut-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }

        public PdfFixture(bool passwordProtected = false)
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "中文 测试.pdf");
            using var pdf = new PdfDocument();
            PdfPage first = pdf.AddPage();
            first.Width = XUnit.FromMillimeter(210);
            first.Height = XUnit.FromMillimeter(297);
            using (XGraphics graphics = XGraphics.FromPdfPage(first))
                graphics.DrawRectangle(XBrushes.Red, 0, 0, first.Width.Point, first.Height.Point);
            PdfPage second = pdf.AddPage();
            second.Width = XUnit.FromMillimeter(100);
            second.Height = XUnit.FromMillimeter(200);
            using (XGraphics graphics = XGraphics.FromPdfPage(second))
                graphics.DrawRectangle(XBrushes.Blue, 0, 0, second.Width.Point, second.Height.Point);
            second.Rotate = 90;
            PdfPage third = pdf.AddPage();
            third.Width = XUnit.FromMillimeter(100);
            third.Height = XUnit.FromMillimeter(200);
            third.CropBox = new PdfRectangle(
                new XPoint(XUnit.FromMillimeter(10).Point, XUnit.FromMillimeter(20).Point),
                new XPoint(XUnit.FromMillimeter(90).Point, XUnit.FromMillimeter(180).Point));
            if (passwordProtected)
                pdf.SecuritySettings.UserPassword = "test-fixture-password";
            pdf.Save(Path);
        }

        public void Dispose()
        {
            File.Delete(Path);
            Directory.Delete(_directory);
        }
    }
}
