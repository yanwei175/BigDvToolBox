using BigDvToolBox_App.Features.RenameByOCR;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class RenameByOcrTests
{
    [TestMethod]
    public async Task EditedTargetName_IsUsedByRenameService()
    {
        string folder = CreateTempFolder();
        try
        {
            string original = Path.Combine(folder, "source.pdf");
            File.WriteAllText(original, "unchanged content");
            RenamePreviewItem item = RenamePlanner.Plan([(original, "934lBG", string.Empty)])[0];

            item.TargetName = "9341BG.pdf";
            Assert.AreEqual(Path.Combine(folder, "9341BG.pdf"), item.TargetPath);
            Assert.IsTrue(File.Exists(original), "Editing the preview must not rename files.");

            BatchSummary result = await RenameFileService.ApplyAsync([item], CancellationToken.None);
            Assert.AreEqual(1, result.Succeeded);
            Assert.AreEqual("unchanged content", await File.ReadAllTextAsync(item.TargetPath));
            Assert.IsFalse(File.Exists(original));
            Assert.IsFalse(item.CanRename);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("../outside.pdf")]
    [DataRow("C:\\outside.pdf")]
    [DataRow("bad:name.pdf")]
    [DataRow("name.pdf ")]
    [DataRow("CON.pdf")]
    public async Task InvalidEditedName_CannotRename(string name)
    {
        string folder = CreateTempFolder();
        try
        {
            string original = Path.Combine(folder, "source.pdf");
            File.WriteAllText(original, "keep");
            RenamePreviewItem item = RenamePlanner.Plan([(original, "initial", string.Empty)])[0];
            item.TargetName = name;

            Assert.IsFalse(item.CanRename);
            Assert.IsFalse(string.IsNullOrEmpty(item.Error));
            BatchSummary result = await RenameFileService.ApplyAsync([item], CancellationToken.None);
            Assert.AreEqual(0, result.Succeeded);
            Assert.IsTrue(File.Exists(original));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void EditingNoChangeOrFailedItem_RefreshesCanApplyAndClearsCorrectedError()
    {
        string folder = CreateTempFolder();
        try
        {
            string original = Path.Combine(folder, "source.pdf");
            File.WriteAllText(original, "keep");
            RenamePreviewItem item = RenamePlanner.Plan([(original, "source", string.Empty)])[0];
            using var viewModel = new RenameByOcrViewModel(new PerPathRenderer());
            viewModel.Results.Add(item);
            bool notified = false;
            viewModel.PropertyChanged += (_, e) => notified |= e.PropertyName == nameof(viewModel.CanApply);

            Assert.IsTrue(item.IsNoChange);
            Assert.IsFalse(viewModel.CanApply);
            item.TargetName = "corrected.pdf";
            Assert.IsFalse(item.IsNoChange);
            Assert.IsTrue(viewModel.CanApply);
            Assert.IsTrue(notified);

            item.TargetName = "bad:name.pdf";
            Assert.IsFalse(viewModel.CanApply);
            item.TargetName = "fixed.pdf";
            Assert.AreEqual(string.Empty, item.Error);
            Assert.IsTrue(viewModel.CanApply);
            item.TargetName = "source.pdf";
            Assert.IsFalse(viewModel.CanApply);
            Assert.AreEqual("无需修改", item.Status);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void ExtractAfterAnchor_HandlesCrLfAndIsCaseInsensitive()
    {
        string result = OcrTextParser.ExtractAfterAnchor("Header\r\nCODE: AB-123\r\nFooter", "Code:");
        Assert.AreEqual("AB-123", result);
    }

    [TestMethod]
    public void ExtractAfterAnchor_HandlesLastLine()
    {
        Assert.AreEqual("ZX-9", OcrTextParser.ExtractAfterAnchor("Title\nCode: ZX-9", "Code:"));
    }

    [TestMethod]
    public void ExtractAfterAnchor_ThrowsWhenAnchorIsMissing()
    {
        Assert.ThrowsExactly<InvalidDataException>(
            () => OcrTextParser.ExtractAfterAnchor("unrelated text", "Code:"));
    }

    [TestMethod]
    public void SanitizeFileStem_RemovesInvalidAndReservedNames()
    {
        Assert.AreEqual("A-B C", RenamePlanner.SanitizeFileStem(" A/B\r\nC. "));
        Assert.AreEqual("CON_", RenamePlanner.SanitizeFileStem("CON"));
        Assert.AreEqual("OCR_Result", RenamePlanner.SanitizeFileStem("..."));
    }

    [TestMethod]
    public void NormalizedRegion_MapsToSourcePixels()
    {
        var region = new NormalizedOcrRegion(0.1, 0.25, 0.5, 0.5);
        var bounds = region.ToPixelBounds(1000, 800);
        Assert.AreEqual((uint)100, bounds.X);
        Assert.AreEqual((uint)200, bounds.Y);
        Assert.AreEqual((uint)500, bounds.Width);
        Assert.AreEqual((uint)400, bounds.Height);
    }

    [TestMethod]
    public void Planner_PreservesExtensionAndResolvesDiskAndBatchConflicts()
    {
        string folder = CreateTempFolder();
        try
        {
            string first = Path.Combine(folder, "first.pdf");
            string second = Path.Combine(folder, "second.jpg");
            File.WriteAllText(first, "a");
            File.WriteAllText(second, "b");
            File.WriteAllText(Path.Combine(folder, "Result.pdf"), "existing");

            IReadOnlyList<RenamePreviewItem> plan = RenamePlanner.Plan(
            [
                (first, "Result", string.Empty),
                (second, "Result", string.Empty),
            ]);

            Assert.AreEqual("Result-1.pdf", plan[0].TargetName);
            Assert.AreEqual("Result.jpg", plan[1].TargetName);
            Assert.IsTrue(File.Exists(first), "Planning must not modify files.");
            Assert.IsTrue(File.Exists(second), "Planning must not modify files.");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplyAsync_RenamesValidItemsAndContinuesAfterFailure()
    {
        string folder = CreateTempFolder();
        try
        {
            string source = Path.Combine(folder, "source.png");
            File.WriteAllText(source, "image");
            var valid = new RenamePreviewItem
            {
                OriginalPath = source,
                TargetPath = Path.Combine(folder, "renamed.png"),
                OcrText = "renamed",
            };
            var invalid = new RenamePreviewItem
            {
                OriginalPath = Path.Combine(folder, "missing.png"),
                TargetPath = Path.Combine(folder, "other.png"),
                OcrText = "other",
            };

            BatchSummary summary = await RenameFileService.ApplyAsync([invalid, valid], CancellationToken.None);
            Assert.AreEqual(1, summary.Succeeded);
            Assert.AreEqual(1, summary.Failed);
            Assert.IsTrue(File.Exists(valid.TargetPath));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task CsvWriter_QuotesCommasQuotesAndNewlines()
    {
        string folder = CreateTempFolder();
        try
        {
            string csv = Path.Combine(folder, "result.csv");
            await RenameFileService.WriteCsvAsync(
                csv,
                [("a,b.pdf", "one \"two\"\nthree")],
                CancellationToken.None);

            string content = await File.ReadAllTextAsync(csv);
            StringAssert.Contains(content, "\"a,b.pdf\"");
            StringAssert.Contains(content, "\"one \"\"two\"\"\nthree\"");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void FileEnumeration_IsTopLevelAndFiltersExtensions()
    {
        string folder = CreateTempFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "a.PDF"), string.Empty);
            File.WriteAllText(Path.Combine(folder, "b.jpeg"), string.Empty);
            File.WriteAllText(Path.Combine(folder, "ignored.txt"), string.Empty);
            string child = Directory.CreateDirectory(Path.Combine(folder, "child")).FullName;
            File.WriteAllText(Path.Combine(child, "nested.png"), string.Empty);

            IReadOnlyList<string> files = RenameFileService.EnumerateSupportedFiles(folder);
            Assert.HasCount(2, files);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void TesseractEngine_InitializesWithBundledLanguageData()
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.X86))
        {
            Assert.Inconclusive("Tesseract native binaries are intentionally unavailable on ARM64.");
        }

        string tessdata = Path.Combine(
            AppContext.BaseDirectory,
            "Features",
            "RenameByOCR",
            "tessdata");
        using var service = new TesseractOcrService(tessdata);
        Assert.IsNotNull(service);
    }

    [TestMethod]
    public async Task CsvWriter_HonorsCancellation()
    {
        string folder = CreateTempFolder();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                RenameFileService.WriteCsvAsync(
                    Path.Combine(folder, "cancelled.csv"),
                    [("file.pdf", "text")],
                    cancellation.Token));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task Renderer_ReportsActualEncodedDimensionsAndCropsAgainstThem()
    {
        string folder = CreateTempFolder();
        try
        {
            string pdfPath = Path.Combine(folder, "fixture.pdf");
            File.WriteAllBytes(pdfPath, CreateSinglePagePdf("9341BG"));
            var renderer = new WindowsDocumentRenderer();

            RenderedDocument document =
                await renderer.RenderAsync(pdfPath, 72, CancellationToken.None);
            (uint decodedWidth, uint decodedHeight) = await DecodePngSizeAsync(document.PngBytes);

            Assert.AreEqual(pdfPath, document.SourcePath);
            Assert.AreEqual(72, document.PdfDpi);
            Assert.AreEqual(decodedWidth, document.PixelWidth);
            Assert.AreEqual(decodedHeight, document.PixelHeight);

            var region = new NormalizedOcrRegion(0.1, 0.2, 0.5, 0.4);
            byte[] crop = await renderer.CropAsync(document, region, CancellationToken.None);
            (uint cropWidth, uint cropHeight) = await DecodePngSizeAsync(crop);
            BitmapBounds expected = region.ToPixelBounds(decodedWidth, decodedHeight);
            Assert.AreEqual(expected.Width, cropWidth);
            Assert.AreEqual(expected.Height, cropHeight);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task CurrentPreviewAndFixedRegionOcr_ReuseTheSameRenderedDocument()
    {
        string folder = CreateTempFolder();
        try
        {
            string pdfPath = Path.Combine(folder, "current.pdf");
            File.WriteAllText(pdfPath, "fixture");
            byte[] previewBytes = [1, 2, 3];
            byte[] croppedBytes = [4, 5, 6];
            var document = new RenderedDocument(pdfPath, previewBytes, 1000, 800, 400);
            var renderer = new RecordingRenderer(document, croppedBytes);
            var ocr = new RecordingOcrService("9341BG");
            using var viewModel = new RenameByOcrViewModel(renderer, () => ocr);

            await viewModel.LoadFolderAsync(folder);
            RenderedDocument? current = viewModel.CurrentDocument;
            viewModel.SetSelectedRegion(new NormalizedOcrRegion(0.1, 0.1, 0.4, 0.2));
            await viewModel.GenerateRenamePreviewAsync();

            Assert.AreSame(document, current);
            Assert.AreSame(previewBytes, viewModel.CurrentPreviewBytes);
            Assert.AreSame(current, renderer.CroppedDocument);
            Assert.AreSame(croppedBytes, ocr.RecognizedBytes);
            Assert.AreEqual(1, renderer.RenderCount);
            Assert.AreEqual("9341BG", viewModel.Results.Single().OcrText);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task SwitchingFiles_ReplacesDocumentContextWithMatchingSourcePath()
    {
        string folder = CreateTempFolder();
        try
        {
            string first = Path.Combine(folder, "a.pdf");
            string second = Path.Combine(folder, "b.pdf");
            File.WriteAllText(first, "first");
            File.WriteAllText(second, "second");
            var renderer = new PerPathRenderer();
            using var viewModel = new RenameByOcrViewModel(renderer);

            await viewModel.LoadFolderAsync(folder);
            RenderedDocument? firstDocument = viewModel.CurrentDocument;
            await viewModel.SelectNextAsync();

            Assert.IsNotNull(firstDocument);
            Assert.AreEqual(first, firstDocument.SourcePath);
            Assert.IsNotNull(viewModel.CurrentDocument);
            Assert.AreEqual(second, viewModel.CurrentDocument.SourcePath);
            Assert.AreEqual(viewModel.CurrentFilePath, viewModel.CurrentDocument.SourcePath);
            Assert.AreNotSame(firstDocument, viewModel.CurrentDocument);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task KnownAquavitLabels_RecognizeExpectedCodes()
    {
        string? folder = Environment.GetEnvironmentVariable("BIGDV_OCR_FIXTURE_DIR");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Assert.Inconclusive("Set BIGDV_OCR_FIXTURE_DIR to run the local six-label OCR regression.");
        }

        Dictionary<string, string> expected = new(StringComparer.OrdinalIgnoreCase)
        {
            ["9341BG-Label.pdf"] = "9341BG",
            ["9341CH-Label.pdf"] = "9341CH",
            ["9341MB-Label.pdf"] = "9341MB",
            ["9346BG-Label.pdf"] = "9346BG",
            ["9346CH-Label.pdf"] = "9346CH",
            ["9346MB-Label.pdf"] = "9346MB",
        };
        var renderer = new WindowsDocumentRenderer();
        string tessdata = Path.Combine(
            AppContext.BaseDirectory,
            "Features",
            "RenameByOCR",
            "tessdata");
        using var ocr = new TesseractOcrService(tessdata);
        var region = new NormalizedOcrRegion(0.0321, 0.0726, 0.3356, 0.1627);

        foreach ((string fileName, string expectedText) in expected)
        {
            string path = Path.Combine(folder, fileName);
            Assert.IsTrue(File.Exists(path), $"Missing OCR fixture: {path}");
            RenderedDocument document =
                await renderer.RenderAsync(path, 72, CancellationToken.None);
            byte[] crop = await renderer.CropAsync(document, region, CancellationToken.None);
            string actual = await ocr.RecognizeAsync(
                crop,
                CancellationToken.None,
                OcrSegmentationMode.SingleWord);
            Assert.AreEqual(expectedText, actual, fileName);
        }
    }

    private static async Task<(uint Width, uint Height)> DecodePngSizeAsync(byte[] pngBytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        return (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
    }

    private static byte[] CreateSinglePagePdf(string text)
    {
        string content = $"BT /F1 32 Tf 20 120 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 170] " +
            "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
        ];

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n")
                .Append(objects[index]).Append("\nendobj\n");
        }

        int xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append('\n')
            .Append("0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1))
        {
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1)
            .Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string CreateTempFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), "BigDvToolBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingRenderer(
        RenderedDocument document,
        byte[] croppedBytes) : IDocumentRenderer
    {
        public int RenderCount { get; private set; }
        public RenderedDocument? CroppedDocument { get; private set; }

        public Task<RenderedDocument> RenderAsync(
            string path,
            int pdfDpi,
            CancellationToken cancellationToken)
        {
            RenderCount++;
            return Task.FromResult(document);
        }

        public Task<byte[]> CropAsync(
            RenderedDocument source,
            NormalizedOcrRegion region,
            CancellationToken cancellationToken)
        {
            CroppedDocument = source;
            return Task.FromResult(croppedBytes);
        }
    }

    private sealed class RecordingOcrService(string text) : IOcrService
    {
        public byte[]? RecognizedBytes { get; private set; }

        public Task<string> RecognizeAsync(
            byte[] pngBytes,
            CancellationToken cancellationToken,
            OcrSegmentationMode segmentationMode = OcrSegmentationMode.Auto)
        {
            RecognizedBytes = pngBytes;
            return Task.FromResult(text);
        }

        public void Dispose()
        {
        }
    }

    private sealed class PerPathRenderer : IDocumentRenderer
    {
        public Task<RenderedDocument> RenderAsync(
            string path,
            int pdfDpi,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RenderedDocument(path, [1], 100, 100, pdfDpi));

        public Task<byte[]> CropAsync(
            RenderedDocument document,
            NormalizedOcrRegion region,
            CancellationToken cancellationToken) =>
            Task.FromResult(document.PngBytes);
    }
}
