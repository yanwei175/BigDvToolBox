using BigDvToolBox_App.Features.PdfLayOut;
using BigDvToolBox_App.Features.PdfOutlineFonts;
using BigDvToolBox_App.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System.Text;

namespace BigDvToolBox.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PdfFontWorkflowTests
{
    [TestMethod]
    [DataRow("/FontFile")]
    [DataRow("/FontFile2")]
    [DataRow("/FontFile3")]
    public void Inspector_RecognizesFontPrograms(string programKey)
    {
        using var pdf = new PdfDocument();
        PdfPage page = pdf.AddPage();
        PdfDictionary font = AddFont(pdf, page, "Embedded");
        var program = new PdfDictionary(pdf);
        program.CreateStream([1, 2, 3]);
        var descriptor = new PdfDictionary(pdf);
        descriptor.Elements[programKey] = program;
        font.Elements["/FontDescriptor"] = descriptor;
        Assert.IsEmpty(PdfLayOutFontInspector.Inspect(pdf, CancellationToken.None));
    }

    [TestMethod]
    public void Inspector_FindsInheritedAndNestedFontsAndDeduplicatesPageNumbers()
    {
        using var pdf = new PdfDocument();
        PdfPage first = pdf.AddPage();
        AddFont(pdf, first, "Missing");
        PdfDictionary inherited = first.Elements.GetDictionary("/Resources")!;
        first.Elements.Remove("/Resources");
        first.Elements.GetDictionary("/Parent")!.Elements["/Resources"] = inherited;
        PdfPage second = pdf.AddPage();
        var form = new PdfDictionary(pdf);
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/Resources"] = inherited;
        var objects = new PdfDictionary(pdf);
        objects.Elements["/Form1"] = form;
        objects.Elements["/Form2"] = form;
        var resources = new PdfDictionary(pdf);
        resources.Elements["/XObject"] = objects;
        second.Elements["/Resources"] = resources;

        IReadOnlyList<PdfLayOutFontIssue> issues = PdfLayOutFontInspector.Inspect(pdf, CancellationToken.None);
        Assert.HasCount(1, issues);
        Assert.AreEqual("Missing", issues[0].FontName);
        CollectionAssert.AreEqual(new[] { 1, 2 }, issues[0].PageNumbers.ToArray());
    }

    [TestMethod]
    public void Inspector_ChecksType0DescendantsAndType3Resources()
    {
        using var pdf = new PdfDocument();
        PdfPage page = pdf.AddPage();
        PdfDictionary font = AddFont(pdf, page, "CID");
        font.Elements.SetName("/Subtype", "/Type0");
        var descendant = new PdfDictionary(pdf);
        var descendants = new PdfArray(pdf);
        descendants.Elements.Add(descendant);
        font.Elements["/DescendantFonts"] = descendants;
        Assert.HasCount(1, PdfLayOutFontInspector.Inspect(pdf, CancellationToken.None));
        var descriptor = new PdfDictionary(pdf);
        var program = new PdfDictionary(pdf);
        program.CreateStream([1, 2, 3]);
        descriptor.Elements["/FontFile2"] = program;
        descendant.Elements["/FontDescriptor"] = descriptor;
        Assert.IsEmpty(PdfLayOutFontInspector.Inspect(pdf, CancellationToken.None));
        font.Elements.SetName("/Subtype", "/Type3");
        font.Elements.Remove("/DescendantFonts");
        Assert.IsEmpty(PdfLayOutFontInspector.Inspect(pdf, CancellationToken.None));
    }

    [TestMethod]
    public void Inspector_HandlesResourceAliasesAndAppearanceFonts()
    {
        using var pdf = new PdfDocument();
        PdfPage page = pdf.AddPage();
        PdfDictionary font = AddFont(pdf, page, "AppearanceFont");
        PdfDictionary resources = page.Elements.GetDictionary("/Resources")!;
        PdfDictionary fonts = resources.Elements.GetDictionary("/Font")!;
        fonts.Elements.Remove("/F1");
        fonts.Elements["/P"] = font.Reference!;
        fonts.Elements["/Type"] = font.Reference!;
        var appearance = new PdfDictionary(pdf);
        appearance.Elements["/Resources"] = resources;
        appearance.Elements["/P"] = page.Reference!;
        var states = new PdfDictionary(pdf);
        states.Elements["/N"] = appearance;
        var annotation = new PdfDictionary(pdf);
        annotation.Elements["/AP"] = states;
        var annotations = new PdfArray(pdf);
        annotations.Elements.Add(annotation);
        page.Elements.Remove("/Resources");
        page.Elements["/Annots"] = annotations;
        Assert.HasCount(1, PdfLayOutFontInspector.Inspect(pdf, CancellationToken.None));
    }

    [TestMethod]
    public async Task Loading_PopulatesTheUnembeddedFontWarning()
    {
        string path = Path.Combine(Path.GetTempPath(), $"font-check-{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllBytesAsync(path, CreatePdf());
            PdfLayOutDocument document = await new WindowsPdfLayOutDocumentService().LoadAsync(path, CancellationToken.None);
            Assert.HasCount(1, document.UnembeddedFonts);
            Assert.AreEqual("Helvetica", document.UnembeddedFonts[0].FontName);
            CollectionAssert.AreEqual(new[] { 1, 2 }, document.UnembeddedFonts[0].PageNumbers.ToArray());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task ExportOption_DoesNotChangePrintOrPresets()
    {
        var capture = new CapturingServices();
        var model = new PdfLayOutViewModel(null, capture, capture);
        model.SetSourceDocument(new("input.pdf", [new(0, new(100, 100))]));
        Assert.IsFalse(model.HasUnsavedPresetChanges);
        model.OutlineFontsOnExport = true;
        await model.ExportPdfCommand.ExecuteAsync("output.pdf");
        Assert.IsTrue(capture.ExportJob!.ExportOptions.OutlineFonts);
        await model.PrintCommand.ExecuteAsync(null);
        Assert.IsFalse(capture.PrintJob!.ExportOptions.OutlineFonts);
        Assert.IsFalse(model.HasUnsavedPresetChanges);
    }

    [TestMethod]
    public void FontWarning_ClearsWhenACompleteDocumentReplacesIt()
    {
        var model = new PdfLayOutViewModel();
        model.SetSourceDocument(new("input.pdf", [new(0, new(100, 100))],
            [new("Missing", new[] { 1 })]));
        Assert.IsTrue(model.HasFontWarning);
        StringAssert.Contains(model.FontWarningMessage, "Missing");
        model.SetSourceDocument(new("complete.pdf", [new(0, new(100, 100))]));
        Assert.IsFalse(model.HasFontWarning);
        Assert.AreEqual(string.Empty, model.FontWarningMessage);
    }

    [TestMethod]
    public async Task MemoryOutline_PreservesPageSizesAndVectorContentWithoutFonts()
    {
        byte[] bytes = CreatePdf();
        var converter = new GhostscriptService();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var input = new MemoryStream([0, 1, 2, .. bytes]);
            input.Position = 3;
            using var output = new MemoryStream();
            await converter.OutlineAsync(input, output, CancellationToken.None);
            Assert.IsTrue(input.CanRead && output.CanWrite, "转换器不能关闭调用方的流。");
            output.Position = 0;
            using PdfDocument result = PdfReader.Open(output, PdfDocumentOpenMode.Import);
            Assert.AreEqual(2, result.PageCount);
            Assert.AreEqual(210, result.Pages[0].Width.Millimeter, 0.01);
            Assert.AreEqual(297, result.Pages[0].Height.Millimeter, 0.01);
            Assert.AreEqual(297, result.Pages[1].Width.Millimeter, 0.01);
            Assert.AreEqual(210, result.Pages[1].Height.Millimeter, 0.01);
            PdfDictionary[] objects = result.Internals.GetAllObjects().OfType<PdfDictionary>().ToArray();
            Assert.IsFalse(objects.Any(item => item.Elements.GetName("/Type") == "/Font"));
            Assert.IsFalse(objects.Any(item => item.Elements.GetName("/Subtype") == "/Image"), "矢量文字和图形不能整页栅格化。");
            Assert.IsEmpty(PdfLayOutFontInspector.Inspect(result, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task MemoryOutline_CancellationAndWriteFailureDoNotBreakTheNextConversion()
    {
        var converter = new GhostscriptService();
        using var input = new MemoryStream(CreatePdf());
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<OperationCanceledException>(() => converter.OutlineAsync(input, output, new CancellationToken(true)));
        Assert.AreEqual(0, output.Length);
        using var failing = new FailingOutput();
        await Assert.ThrowsAsync<IOException>(() => converter.OutlineAsync(input, failing, CancellationToken.None));
        input.Position = 0;
        await converter.OutlineAsync(input, output, CancellationToken.None);
        Assert.IsTrue(output.Length > 100);
    }

    [TestMethod]
    public async Task Export_OutlineFailurePreservesExistingFileAndCleansUpAtomicSave()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BigDvToolBox.FontTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "output.pdf");
        try
        {
            byte[] oldContent = [1, 2, 3];
            await File.WriteAllBytesAsync(target, oldContent);
            var job = new PdfLayOutJob(new("input.pdf", [new(0, new(100, 100))]), new())
            { ExportOptions = new() { OutlineFonts = true } };
            var service = new PdfSharpLayOutExportService(new FakeGenerator(), new FailingOutliner());
            await Assert.ThrowsAsync<IOException>(() => service.ExportAsync(job, target, CancellationToken.None));
            CollectionAssert.AreEqual(oldContent, await File.ReadAllBytesAsync(target));
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally { File.Delete(target); Directory.Delete(directory); }
    }

    private static PdfDictionary AddFont(PdfDocument pdf, PdfPage page, string name)
    {
        var font = new PdfDictionary(pdf);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/" + name);
        pdf.Internals.AddObject(font);
        var fonts = new PdfDictionary(pdf);
        fonts.Elements["/F1"] = font.Reference!;
        var resources = new PdfDictionary(pdf);
        resources.Elements["/Font"] = fonts;
        page.Elements["/Resources"] = resources;
        return font;
    }

    private static byte[] CreatePdf()
    {
        using var pdf = new PdfDocument();
        foreach (var size in new[] { new PdfLayOutSize(210, 297), new PdfLayOutSize(297, 210) })
        {
            PdfPage page = pdf.AddPage();
            page.Width = XUnit.FromMillimeter(size.Width);
            page.Height = XUnit.FromMillimeter(size.Height);
            AddFont(pdf, page, "Helvetica");
            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("0 0 0 rg 20 20 80 40 re f BT /F1 24 Tf 72 100 Td (MEMORY VECTOR) Tj ET"));
        }
        using var stream = new MemoryStream();
        pdf.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private sealed class FailingOutput : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("模拟输出错误");
    }

    private sealed class FakeGenerator : IPdfLayOutPdfGenerator
    {
        public Task GenerateAsync(PdfLayOutJob job, Stream output, CancellationToken token) =>
            output.WriteAsync(CreatePdf(), token).AsTask();
    }

    private sealed class CapturingServices : IPdfLayOutExportService, IPdfLayOutPrintService
    {
        internal PdfLayOutJob? ExportJob { get; private set; }
        internal PdfLayOutJob? PrintJob { get; private set; }
        public Task ExportAsync(PdfLayOutJob job, string path, CancellationToken token)
        {
            ExportJob = job;
            return Task.CompletedTask;
        }
        public Task<PdfLayOutPrintResult> PrintAsync(PdfLayOutJob job, CancellationToken token)
        {
            PrintJob = job;
            return Task.FromResult(PdfLayOutPrintResult.Cancelled);
        }
    }

    private sealed class FailingOutliner : IPdfFontOutliner
    {
        public Task OutlineAsync(Stream input, Stream output, CancellationToken token) =>
            Task.FromException(new IOException("模拟转曲失败"));
    }
}
