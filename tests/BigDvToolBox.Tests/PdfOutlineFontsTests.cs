using BigDvToolBox_App.Features.PdfOutlineFonts;
using System.Runtime.InteropServices;
using System.Text;

namespace BigDvToolBox.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PdfOutlineFontsTests
{
    [TestMethod]
    public void FileRules_FilterPdfAndBuildExpectedOutputPath()
    {
        string folder = CreateTempFolder();
        try
        {
            string pdf = Path.Combine(folder, "设计 稿.PDF");
            string text = Path.Combine(folder, "notes.txt");
            File.WriteAllText(pdf, "pdf");
            File.WriteAllText(text, "text");

            Assert.IsTrue(PdfOutlineFileRules.IsPdfFile(pdf));
            Assert.IsFalse(PdfOutlineFileRules.IsPdfFile(text));
            Assert.AreEqual(
                Path.Combine(folder, "设计 稿_outlined.pdf"),
                PdfOutlineFileRules.GetOutputPath(pdf, null));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void ViewModel_AddFiles_DeduplicatesCaseInsensitivePaths()
    {
        string folder = CreateTempFolder();
        try
        {
            string pdf = Path.Combine(folder, "sample.pdf");
            File.WriteAllText(pdf, "pdf");
            var viewModel = new PdfOutlineFontsViewModel(new FakeGhostscriptService());

            int added = viewModel.AddFiles([pdf, pdf.ToUpperInvariant()]);

            Assert.AreEqual(1, added);
            Assert.HasCount(1, viewModel.Files);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void GhostscriptArguments_ContainOutlineAndImageOptions()
    {
        string[] vector = GhostscriptService.BuildArguments(
            "输入.pdf",
            "输出.pdf",
            PdfConvertMode.Vector,
            600);
        CollectionAssert.Contains(vector, "-dNoOutputFonts");
        CollectionAssert.Contains(vector, "-sDEVICE=pdfwrite");

        string[] image = GhostscriptService.BuildArguments(
            "输入.pdf",
            "输出.pdf",
            PdfConvertMode.Image,
            1500);
        CollectionAssert.Contains(image, "-sDEVICE=pdfimage24");
        CollectionAssert.Contains(image, "-r1200");
    }

    [TestMethod]
    public async Task BatchConversion_ContinuesAfterSingleFileFailure()
    {
        string folder = CreateTempFolder();
        try
        {
            string first = Path.Combine(folder, "first.pdf");
            string second = Path.Combine(folder, "second.pdf");
            File.WriteAllText(first, "first");
            File.WriteAllText(second, "second");

            var service = new FakeGhostscriptService(second);
            var viewModel = new PdfOutlineFontsViewModel(service);
            viewModel.AddFiles([first, second]);
            await viewModel.InitializeAsync();

            PdfBatchSummary summary = await viewModel.StartConversionAsync();

            Assert.AreEqual(2, summary.Total);
            Assert.AreEqual(1, summary.Succeeded);
            Assert.AreEqual(1, summary.Failed);
            Assert.AreEqual(PdfFileState.Completed, viewModel.Files[0].State);
            Assert.AreEqual(PdfFileState.Failed, viewModel.Files[1].State);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public async Task BatchConversion_CancelsActiveAndQueuedFiles()
    {
        string folder = CreateTempFolder();
        try
        {
            string first = Path.Combine(folder, "first.pdf");
            string second = Path.Combine(folder, "second.pdf");
            File.WriteAllText(first, "first");
            File.WriteAllText(second, "second");

            var service = new CancellableGhostscriptService();
            var viewModel = new PdfOutlineFontsViewModel(service)
            {
                SelectedThreadCount = 1,
            };
            viewModel.AddFiles([first, second]);
            await viewModel.InitializeAsync();

            Task<PdfBatchSummary> conversion = viewModel.StartConversionAsync();
            await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewModel.Cancel();
            PdfBatchSummary summary = await conversion;

            Assert.AreEqual(2, summary.Cancelled);
            Assert.IsTrue(viewModel.Files.All(item => item.State == PdfFileState.Cancelled));
            Assert.IsFalse(viewModel.IsConverting);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void NativeSupport_IsLimitedToX86AndX64()
    {
        bool expected = RuntimeInformation.ProcessArchitecture
            is Architecture.X86 or Architecture.X64;
        Assert.AreEqual(expected, new GhostscriptService().IsSupported);
    }

    [TestMethod]
    public async Task BundledGhostscript_ConvertsVectorAndImagePdf()
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X86 or Architecture.X64))
        {
            Assert.Inconclusive("ARM64 原生进程不加载 x86/x64 Ghostscript。");
        }

        string folder = CreateTempFolder();
        try
        {
            string source = Path.Combine(folder, "中文 输入.pdf");
            string vectorOutput = Path.Combine(folder, "中文 矢量.pdf");
            string imageOutput = Path.Combine(folder, "中文 图片.pdf");
            await File.WriteAllBytesAsync(source, CreateMinimalPdf());

            var service = new GhostscriptService();
            await service.InitializeAsync(CancellationToken.None);
            await service.ConvertAsync(
                source,
                vectorOutput,
                PdfConvertMode.Vector,
                300,
                CancellationToken.None);
            await service.ConvertAsync(
                source,
                imageOutput,
                PdfConvertMode.Image,
                150,
                CancellationToken.None);

            Assert.IsTrue(new FileInfo(vectorOutput).Length > 100);
            Assert.IsTrue(new FileInfo(imageOutput).Length > 100);
            CollectionAssert.AreEqual(
                Encoding.ASCII.GetBytes("%PDF"),
                (await File.ReadAllBytesAsync(vectorOutput))[..4]);
            CollectionAssert.AreEqual(
                Encoding.ASCII.GetBytes("%PDF"),
                (await File.ReadAllBytesAsync(imageOutput))[..4]);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static byte[] CreateMinimalPdf()
    {
        const string content = "BT /F1 24 Tf 72 720 Td (Hello PDF) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];

        var bytes = new List<byte>();
        var offsets = new List<int> { 0 };

        void Append(string value) => bytes.AddRange(Encoding.ASCII.GetBytes(value));

        Append("%PDF-1.4\n");
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(bytes.Count);
            Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        int xrefOffset = bytes.Count;
        Append($"xref\n0 {objects.Length + 1}\n");
        Append("0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1))
        {
            Append($"{offset:0000000000} 00000 n \n");
        }

        Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        Append($"startxref\n{xrefOffset}\n%%EOF\n");
        return [.. bytes];
    }

    private static string CreateTempFolder()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "BigDvToolBox.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeGhostscriptService(string? failingPath = null)
        : IGhostscriptService
    {
        public string? VersionText => GhostscriptService.GhostscriptVersion;

        public bool IsSupported => true;

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ConvertAsync(
            string inputPath,
            string outputPath,
            PdfConvertMode convertMode,
            int dpi,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return string.Equals(inputPath, failingPath, StringComparison.OrdinalIgnoreCase)
                ? Task.FromException(new InvalidOperationException("模拟转换失败"))
                : Task.CompletedTask;
        }
    }

    private sealed class CancellableGhostscriptService : IGhostscriptService
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? VersionText => GhostscriptService.GhostscriptVersion;

        public bool IsSupported => true;

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async Task ConvertAsync(
            string inputPath,
            string outputPath,
            PdfConvertMode convertMode,
            int dpi,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
