using System.Text;
using Tesseract;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BigDvToolBox_App.Features.RenameByOCR;

internal interface IDocumentRenderer
{
    Task<RenderedDocument> RenderAsync(string path, int pdfDpi, CancellationToken cancellationToken);
    Task<byte[]> CropAsync(RenderedDocument document, NormalizedOcrRegion region, CancellationToken cancellationToken);
}

internal interface IOcrService : IDisposable
{
    Task<string> RecognizeAsync(byte[] pngBytes, CancellationToken cancellationToken);
}

internal sealed class WindowsDocumentRenderer : IDocumentRenderer
{
    public async Task<RenderedDocument> RenderAsync(
        string path,
        int pdfDpi,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        string extension = Path.GetExtension(path);

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            PdfDocument pdf = await PdfDocument.LoadFromFileAsync(file);
            if (pdf.PageCount == 0)
            {
                throw new InvalidDataException("PDF 不包含页面。");
            }

            using PdfPage page = pdf.GetPage(0);
            double scale = Math.Clamp(pdfDpi, 72, 1200) / 72d;
            uint width = Math.Max(1, (uint)Math.Round(page.Size.Width * scale));
            uint height = Math.Max(1, (uint)Math.Round(page.Size.Height * scale));
            using var stream = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = width,
                DestinationHeight = height,
                BitmapEncoderId = BitmapEncoder.PngEncoderId,
            };
            await page.RenderToStreamAsync(stream, options);
            return new RenderedDocument(await ReadBytesAsync(stream), width, height);
        }

        using IRandomAccessStream imageStream = await file.OpenAsync(FileAccessMode.Read);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(imageStream);
        byte[] png = await EncodeAsync(decoder, null, cancellationToken);
        return new RenderedDocument(png, decoder.PixelWidth, decoder.PixelHeight);
    }

    public async Task<byte[]> CropAsync(
        RenderedDocument document,
        NormalizedOcrRegion region,
        CancellationToken cancellationToken)
    {
        if (!region.IsValid)
        {
            throw new InvalidOperationException("请先在预览图中框选有效的 OCR 区域。");
        }

        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(document.PngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        input.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
        return await EncodeAsync(decoder, region.ToPixelBounds(document.PixelWidth, document.PixelHeight), cancellationToken);
    }

    private static async Task<byte[]> EncodeAsync(
        BitmapDecoder decoder,
        BitmapBounds? bounds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transform = new BitmapTransform();
        if (bounds is BitmapBounds crop)
        {
            transform.Bounds = crop;
        }

        SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        using (bitmap)
        using (var output = new InMemoryRandomAccessStream())
        {
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            return await ReadBytesAsync(output);
        }
    }

    private static async Task<byte[]> ReadBytesAsync(IRandomAccessStream stream)
    {
        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        var bytes = new byte[stream.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}

internal sealed class TesseractOcrService : IOcrService
{
    private readonly TesseractEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TesseractOcrService(string tessdataPath)
    {
        _engine = new TesseractEngine(tessdataPath, "eng+ces+pol+rus", EngineMode.Default);
    }

    public async Task<string> RecognizeAsync(byte[] pngBytes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Pix image = Pix.LoadFromMemory(pngBytes);
                using Page page = _engine.Process(image);
                return page.GetText().Trim();
            }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _engine.Dispose();
        _gate.Dispose();
    }
}

internal static class OcrTextParser
{
    public static string ExtractAfterAnchor(string text, string anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            throw new ArgumentException("请输入锚定文本。", nameof(anchor));
        }

        int start = text.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            throw new InvalidDataException($"未找到锚定文本“{anchor}”。");
        }

        start += anchor.Length;
        int end = text.IndexOfAny(['\r', '\n'], start);
        if (end < 0)
        {
            end = text.Length;
        }

        string result = text[start..end].Trim();
        if (result.Length == 0)
        {
            throw new InvalidDataException("锚定文本后没有可用于文件名的内容。");
        }

        return result;
    }
}

internal static class RenamePlanner
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string SanitizeFileStem(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value.Normalize(NormalizationForm.FormKC))
        {
            if (character is '\r' or '\n' or '\t')
            {
                builder.Append(' ');
            }
            else if (Path.GetInvalidFileNameChars().Contains(character) || char.IsControl(character))
            {
                builder.Append('-');
            }
            else
            {
                builder.Append(character);
            }
        }

        string result = string.Join(' ', builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .TrimEnd('.', ' ');

        if (result.Length == 0)
        {
            result = "OCR_Result";
        }

        if (ReservedDeviceNames.Contains(result))
        {
            result += "_";
        }

        const int maxStemLength = 180;
        return result.Length > maxStemLength
            ? result[..maxStemLength].TrimEnd('.', ' ')
            : result;
    }

    public static IReadOnlyList<RenamePreviewItem> Plan(
        IReadOnlyList<(string Path, string Text, string Error)> recognized)
    {
        var reserved = new HashSet<string>(
            recognized.SelectMany(item =>
            {
                string? directory = Path.GetDirectoryName(item.Path);
                return directory is null || !Directory.Exists(directory)
                    ? []
                    : Directory.EnumerateFiles(directory);
            }),
            StringComparer.OrdinalIgnoreCase);

        var results = new List<RenamePreviewItem>(recognized.Count);
        foreach ((string originalPath, string text, string error) in recognized)
        {
            if (!string.IsNullOrEmpty(error))
            {
                results.Add(new RenamePreviewItem
                {
                    OriginalPath = originalPath,
                    OcrText = text,
                    Error = error,
                    Status = "识别失败",
                });
                continue;
            }

            string directory = Path.GetDirectoryName(originalPath)!;
            string extension = Path.GetExtension(originalPath);
            string stem = SanitizeFileStem(text);
            string target = Path.Combine(directory, stem + extension);
            int suffix = 1;

            while (reserved.Contains(target) &&
                   !target.Equals(originalPath, StringComparison.OrdinalIgnoreCase))
            {
                target = Path.Combine(directory, $"{stem}-{suffix++}{extension}");
            }

            bool noChange = target.Equals(originalPath, StringComparison.OrdinalIgnoreCase);
            reserved.Add(target);
            results.Add(new RenamePreviewItem
            {
                OriginalPath = originalPath,
                OcrText = text,
                TargetPath = target,
                IsNoChange = noChange,
                Status = noChange ? "无需修改" : "待确认",
            });
        }

        return results;
    }
}

internal static class RenameFileService
{
    private static readonly string[] SupportedExtensions = [".pdf", ".png", ".jpg", ".jpeg"];

    public static IReadOnlyList<string> EnumerateSupportedFiles(string folderPath) =>
        Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public static async Task<BatchSummary> ApplyAsync(
        IEnumerable<RenamePreviewItem> items,
        CancellationToken cancellationToken)
    {
        int succeeded = 0;
        int skipped = 0;
        int failed = 0;

        foreach (RenamePreviewItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(item.Error) || item.IsNoChange)
            {
                item.Status = item.IsNoChange ? "无需修改" : "已跳过";
                skipped++;
                continue;
            }

            try
            {
                await Task.Run(() => File.Move(item.OriginalPath, item.TargetPath), cancellationToken);
                item.Status = "已重命名";
                succeeded++;
            }
            catch (Exception exception)
            {
                item.Error = exception.Message;
                item.Status = "重命名失败";
                failed++;
            }
        }

        return new BatchSummary(succeeded, skipped, failed);
    }

    public static async Task WriteCsvAsync(
        string path,
        IEnumerable<(string FileName, string Text)> rows,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("\"文件名\",\"OCR 文本\"");
        foreach ((string fileName, string text) in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync($"\"{Escape(fileName)}\",\"{Escape(text)}\"");
        }
    }

    private static string Escape(string value) => value.Replace("\"", "\"\"");
}
