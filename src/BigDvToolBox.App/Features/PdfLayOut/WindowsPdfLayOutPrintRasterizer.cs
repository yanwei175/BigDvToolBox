using System.Runtime.InteropServices;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BigDvToolBox_App.Features.PdfLayOut;

internal sealed record PdfLayOutPrintBitmap(int Width, int Height, byte[] BgraPixels);

/// <summary>只保留当前印张的像素。读取的是已包含裁切、缩放及印刷标记的拼版 PDF。</summary>
internal sealed class WindowsPdfLayOutPrintRasterizer : IDisposable
{
    private readonly IRandomAccessStream _input;
    private readonly PdfDocument _document;
    public int PageCount => checked((int)_document.PageCount);

    private WindowsPdfLayOutPrintRasterizer(IRandomAccessStream input, PdfDocument document) => (_input, _document) = (input, document);

    public static async Task<WindowsPdfLayOutPrintRasterizer> OpenAsync(Stream pdfStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);
        if (!pdfStream.CanRead || !pdfStream.CanSeek) throw new ArgumentException("PDF 输入流必须可读且可定位。", nameof(pdfStream));
        cancellationToken.ThrowIfCancellationRequested();
        pdfStream.Position = 0;
        IRandomAccessStream input = new BorrowedStream(pdfStream).AsRandomAccessStream();
        try
        {
            PdfDocument pdf = await PdfDocument.LoadFromStreamAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
            return new(input, pdf);
        }
        catch { input.Dispose(); throw; }
    }

    public async Task<PdfLayOutPrintBitmap> RenderAsync(int pageIndex, PdfLayOutPrintRasterSize size, CancellationToken cancellationToken)
    {
        if (pageIndex < 0 || pageIndex >= PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (size.Width <= 0 || size.Height <= 0 || size.Width > PdfLayOutPrintGeometry.MaximumRasterEdge ||
            size.Height > PdfLayOutPrintGeometry.MaximumRasterEdge || (long)size.Width * size.Height > PdfLayOutPrintGeometry.MaximumRasterPixels)
            throw new ArgumentOutOfRangeException(nameof(size));
        cancellationToken.ThrowIfCancellationRequested();
        using PdfPage page = _document.GetPage((uint)pageIndex);
        using var output = new InMemoryRandomAccessStream();
        // 系统 PDF 引擎把 DestinationWidth/Height 当作 DIP。先消除桌面 DPI，避免生成多倍尺寸的临时位图。
        double desktopScale = Math.Max(1, GetDpiForSystem() / 96d);
        await page.RenderToStreamAsync(output, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Ceiling(size.Width / desktopScale)),
            DestinationHeight = (uint)Math.Max(1, Math.Ceiling(size.Height / desktopScale)),
            BitmapEncoderId = BitmapEncoder.PngEncoderId,
            BackgroundColor = new() { A = 255, R = 255, G = 255, B = 255 },
            IsIgnoringHighContrast = true,
        }).AsTask(cancellationToken).ConfigureAwait(false);
        output.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(output).AsTask(cancellationToken).ConfigureAwait(false);
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform
            {
                ScaledWidth = (uint)size.Width, ScaledHeight = (uint)size.Height,
                InterpolationMode = BitmapInterpolationMode.Fant,
            }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)
            .AsTask(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(size.Width, size.Height, pixels.DetachPixelData());
    }

    public void Dispose() => _input.Dispose();

    // WinRT 适配器可以释放自己，但输入流始终由创建打印任务的调用方拥有。
    private sealed class BorrowedStream(Stream source) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForSystem();
}
