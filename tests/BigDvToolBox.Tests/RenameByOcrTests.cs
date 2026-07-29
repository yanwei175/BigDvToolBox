using BigDvToolBox_App.Features.RenameByOCR;
using System.Runtime.InteropServices;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class RenameByOcrTests
{
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

    private static string CreateTempFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), "BigDvToolBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
