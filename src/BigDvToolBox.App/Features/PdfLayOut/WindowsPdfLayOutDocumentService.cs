using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>使用系统 PDF 引擎读取尺寸和渲染，并通过 PDFsharp 补齐 UserUnit。文件资源均按次释放。</summary>
public sealed class WindowsPdfLayOutDocumentService : IPdfLayOutDocumentService, IPdfLayOutPreviewService
{
    private readonly int _maximumPreviewEdge;

    public WindowsPdfLayOutDocumentService(int maximumPreviewEdge = 1600)
    {
        if (maximumPreviewEdge is < 64 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(maximumPreviewEdge));
        _maximumPreviewEdge = maximumPreviewEdge;
    }

    public Task<PdfLayOutDocument> LoadAsync(string filePath, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            ValidatePath(filePath);
            StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(filePath)).AsTask(cancellationToken).ConfigureAwait(false);
            using IRandomAccessStream input = await file.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
            PdfDocument pdf = await OpenDocumentAsync(input, cancellationToken).ConfigureAwait(false);
            if (pdf.PageCount == 0)
                throw new InvalidDataException("PDF 不包含页面。");
            double[] units = ReadUserUnits(file.Path, cancellationToken);
            if (units.Length != pdf.PageCount)
                throw new InvalidDataException("PDF 页面信息不一致，请重新加载文件。");

            var pages = new List<PdfLayOutSourcePage>(checked((int)pdf.PageCount));
            for (uint index = 0; index < pdf.PageCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using PdfPage page = pdf.GetPage(index);
                // Size 已处理 CropBox/MediaBox 和 Rotation，但系统引擎没有计入 UserUnit。
                pages.Add(new(checked((int)index), new(page.Size.Width * units[index] * 25.4 / 96,
                    page.Size.Height * units[index] * 25.4 / 96)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new PdfLayOutDocument(filePath, pages);
        }, cancellationToken);

    public Task<PdfLayOutPagePreview> RenderAsync(PdfLayOutDocument document, int pageIndex, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            ArgumentNullException.ThrowIfNull(document);
            if (pageIndex < 0 || pageIndex >= document.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));

            StorageFile file = await StorageFile.GetFileFromPathAsync(document.FilePath).AsTask(cancellationToken).ConfigureAwait(false);
            using IRandomAccessStream input = await file.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
            PdfDocument pdf = await OpenDocumentAsync(input, cancellationToken).ConfigureAwait(false);
            if ((uint)pageIndex >= pdf.PageCount)
                throw new InvalidDataException("源 PDF 已发生变化，请重新加载文件。");

            using PdfPage page = pdf.GetPage((uint)pageIndex);
            double[] units = ReadUserUnits(file.Path, cancellationToken);
            if (units.Length != document.PageCount)
                throw new InvalidDataException("源 PDF 页数已发生变化，请重新加载文件。");
            PdfLayOutSize expectedSize = document.Pages[pageIndex].Size;
            double widthMm = page.Size.Width * units[pageIndex] * 25.4 / 96;
            double heightMm = page.Size.Height * units[pageIndex] * 25.4 / 96;
            if (Math.Abs(widthMm - expectedSize.Width) > 0.01 || Math.Abs(heightMm - expectedSize.Height) > 0.01)
                throw new InvalidDataException("源页面尺寸已发生变化，请重新加载文件。");

            double factor = _maximumPreviewEdge / Math.Max(page.Size.Width, page.Size.Height);
            uint width = Math.Max(1, (uint)Math.Round(page.Size.Width * factor));
            uint height = Math.Max(1, (uint)Math.Round(page.Size.Height * factor));
            using var output = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(output, new PdfPageRenderOptions
            {
                DestinationWidth = width,
                DestinationHeight = height,
                BitmapEncoderId = BitmapEncoder.PngEncoderId,
                BackgroundColor = new() { A = 255, R = 255, G = 255, B = 255 },
                IsIgnoringHighContrast = true,
            }).AsTask(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            output.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(output).AsTask(cancellationToken).ConfigureAwait(false);
            // 系统渲染使用 DIP，高 DPI 下返回的像素数可能更大。统一为固定像素尺寸。
            using var normalized = new InMemoryRandomAccessStream();
            IRandomAccessStream result = output;
            if (decoder.PixelWidth != width || decoder.PixelHeight != height)
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateForTranscodingAsync(normalized, decoder).AsTask(cancellationToken).ConfigureAwait(false);
                encoder.BitmapTransform.ScaledWidth = width;
                encoder.BitmapTransform.ScaledHeight = height;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
                result = normalized;
            }
            result.Seek(0);
            using var reader = new DataReader(result.GetInputStreamAt(0));
            uint length = checked((uint)result.Size);
            await reader.LoadAsync(length).AsTask(cancellationToken).ConfigureAwait(false);
            var bytes = new byte[length];
            reader.ReadBytes(bytes);
            cancellationToken.ThrowIfCancellationRequested();
            return new PdfLayOutPagePreview(pageIndex, bytes, width, height);
        }, cancellationToken);

    private static double[] ReadUserUnits(string filePath, CancellationToken cancellationToken)
    {
        using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(filePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var units = new double[pdf.PageCount];
        for (int index = 0; index < units.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elements = pdf.Pages[index].Elements;
            double unit = elements.ContainsKey("/UserUnit") ? elements.GetReal("/UserUnit") : 1;
            if (!double.IsFinite(unit) || unit <= 0)
                throw new InvalidDataException($"第 {index + 1} 页的 UserUnit 无效。");
            units[index] = unit;
        }
        return units;
    }

    private static void ValidatePath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择 PDF 文件。", nameof(filePath));
    }

    private static async Task<PdfDocument> OpenDocumentAsync(IRandomAccessStream input, CancellationToken cancellationToken)
    {
        try
        {
            return await PdfDocument.LoadFromStreamAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidDataException("无法读取 PDF，请确认文件完整且未使用打开密码保护。", exception);
        }
    }
}
