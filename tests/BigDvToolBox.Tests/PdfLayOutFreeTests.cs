using BigDvToolBox_App.Features.PdfLayOut;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutFreeTests
{
    private static PdfLayOutSettings Settings => new()
    {
        Mode = PdfLayOutMode.Free, Columns = 2, Rows = 2, HorizontalGap = 5, VerticalGap = 5,
        Marks = new() { ShowCropMarks = false },
    };

    private static PdfLayOutDocument Document(string name = "one.pdf") => new(name,
        [new(0, new(50, 30)), new(1, new(120, 200))], [new("Helvetica", [2])]);
    private static PdfLayOutCellViewModel Cell(PdfLayOutViewModel model, int row, int column) =>
        model.FreeCells.Single(c => c.Row == row && c.Column == column);
    private static PdfLayOutViewModel Model(Services service) => new(service, service, service)
        { ModeIndex = 1, Columns = 2, Rows = 2 };

    [TestMethod]
    public void EmptyGrid_DoesNotNeedAPdf_AndMixedPagesUseTheirOwnGeometry()
    {
        var empty = PdfLayOutCalculator.CalculateFree(Settings, []);
        Assert.IsTrue(empty.IsValid);
        Assert.HasCount(4, empty.Cells);
        Assert.IsEmpty(empty.Placements);
        Assert.AreEqual(new PdfLayOutRect(0, 0, 102.5, 146), empty.Cells[0].Bounds);
        var doc = Document();
        var layout = PdfLayOutCalculator.CalculateFree(Settings with { Offset = new(7, -3), SourceCrop = new(2, 3, 4, 5) },
            [new(0, 0, doc, 0), new(1, 1, doc, 1)]);
        Assert.HasCount(4, layout.Cells);
        Assert.HasCount(2, layout.Placements);
        var small = layout.Placements[0];
        Assert.AreEqual(1d, small.Scale);
        Assert.AreEqual((102.5 - 44) / 2 + 7, small.ContentBounds.X, 0.001);
        Assert.AreEqual((146 - 22) / 2 - 3, small.ContentBounds.Y, 0.001);
        var large = layout.Placements[1];
        Assert.AreEqual(large.ContentBounds.Intersect(large.CellBounds), large.ClipBounds);
        Assert.IsTrue(large.ClipBounds.Width < large.ContentBounds.Width);
        Assert.AreEqual(doc.FilePath, large.SourceDocumentId);
        var invalid = PdfLayOutCalculator.CalculateFree(Settings, [new(0, 0, doc, 99), new(1, 1, doc, 0)]);
        Assert.IsFalse(invalid.IsValid);
        Assert.HasCount(4, invalid.Cells);
        Assert.HasCount(1, invalid.Placements, "一个无效页不能遮掉其他格的预览。");
    }

    [TestMethod]
    [DataRow(PdfLayOutScaleMode.OriginalSize)]
    [DataRow(PdfLayOutScaleMode.Fit)]
    [DataRow(PdfLayOutScaleMode.Fill)]
    [DataRow(PdfLayOutScaleMode.Custom)]
    public void FreeAndRepeat_UseIdenticalPlacementAndMarkGeometry(PdfLayOutScaleMode mode)
    {
        var settings = Settings with
        {
            GridMode = PdfLayOutGridMode.Manual, SourceCrop = new(2, 3, 4, 5), Offset = new(9, -8),
            ScaleMode = mode, ScalePercent = 170, Marks = new() { ShowCellBorders = true },
        };
        var doc = Document();
        var repeat = PdfLayOutCalculator.Calculate(doc.Pages[1], settings);
        var free = PdfLayOutCalculator.CalculateFree(settings, [new(1, 1, doc, 1)]);
        Assert.AreEqual(repeat.Placements[3], free.Placements.Single() with { SourceDocumentId = string.Empty });
        CollectionAssert.AreEqual(repeat.Marks.SelectMany(m => m.Lines).ToArray(), free.Marks.SelectMany(m => m.Lines).ToArray());
    }

    [TestMethod]
    public async Task Cells_CopyPageReplaceClearAndModesKeepIndependentSources()
    {
        var services = new Services();
        var model = new PdfLayOutViewModel(services, services, services);
        await model.LoadDocumentCommand.ExecuteAsync("repeat.pdf");
        var repeat = model.SourceDocument;
        model.ModeIndex = 1;
        Assert.HasCount(4, model.CurrentLayout!.Cells);
        Assert.IsFalse(model.CanExport);
        var first = Cell(model, 0, 0);
        var second = Cell(model, 1, 1);
        await model.LoadCellAsync(first, "one.pdf");
        model.SelectCellPage(first, 1);
        model.CopyToCell(first.Content!, second);
        Assert.AreEqual(1, second.Content!.SourcePageIndex);
        Assert.AreSame(first.Content!.Document, second.Content.Document);
        Assert.HasCount(2, model.CurrentLayout.Placements);
        StringAssert.Contains(model.FontWarningMessage, "one.pdf");
        StringAssert.Contains(model.FontWarningMessage, "第 2 页");
        model.ClearCell(first);
        Assert.IsNotNull(second.Content);
        model.SelectCellPage(second, 0);
        Assert.IsFalse(model.HasFontWarning);
        model.ModeIndex = 0;
        Assert.AreSame(repeat, model.SourceDocument);
        Assert.IsTrue(model.HasDocument);
        model.ModeIndex = 1;
        Assert.IsNull(first.Content);
        Assert.IsNotNull(second.Content);
        model.SelectCell(second);
        Assert.AreEqual("one.pdf", model.FileName);
        await model.LoadDocumentCommand.ExecuteAsync("replacement.pdf");
        Assert.AreEqual("replacement.pdf", second.Content!.Document.FileName);
        Assert.AreSame(repeat, model.SourceDocument);
        await model.ExportPdfCommand.ExecuteAsync("result.pdf");
        Assert.HasCount(1, services.Job!.Sheets);
        model.ClearCell(second);
        Assert.IsFalse(model.CanExport);
    }

    [TestMethod]
    public async Task ShrinkingGrid_AsksBeforeDeletingAndPreservesCoordinatesOnCancelAndConfirm()
    {
        var model = Model(new Services());
        var first = Cell(model, 0, 0);
        var last = Cell(model, 1, 1);
        await model.LoadCellAsync(first, "one.pdf");
        await model.LoadCellAsync(last, "two.pdf");
        model.Columns = 1;
        var pending = model.PendingLayoutChange!;
        Assert.HasCount(1, pending.RemovedContents);
        Assert.AreEqual(2d, model.Columns);
        Assert.IsFalse(model.CanExport);
        await model.ResolveLayoutChangeAsync(pending, false);
        Assert.HasCount(4, model.FreeCells);
        Assert.AreSame(last, Cell(model, 1, 1));
        model.Rows = 1;
        await model.ResolveLayoutChangeAsync(model.PendingLayoutChange!, true);
        Assert.HasCount(2, model.FreeCells);
        Assert.AreSame(first, Cell(model, 0, 0));
        model.Rows = 2;
        Assert.IsNull(Cell(model, 1, 1).Content);
        Assert.AreEqual("one.pdf", first.Content!.Document.FileName);
    }

    [TestMethod]
    public async Task CellLoads_FailureCancellationReplacementAndModeChangesCannotOverwriteNewerContent()
    {
        var services = new Services();
        var model = Model(services);
        var cell = Cell(model, 0, 0);
        await model.LoadCellAsync(cell, "original.pdf");
        var original = cell.Content;
        services.Load = (_, _) => Task.FromException<PdfLayOutDocument>(new IOException("bad PDF"));
        await model.LoadCellAsync(cell, "bad.pdf");
        Assert.AreSame(original, cell.Content);
        Assert.IsNotNull(model.ErrorMessage);
        var late = new TaskCompletionSource<PdfLayOutDocument>();
        services.Load = (_, _) => late.Task; // 故意忽略取消，覆盖不合作的底层加载器。
        Task loading = model.LoadCellAsync(cell, "late.pdf");
        Assert.IsFalse(model.CanExport);
        services.Load = (p, _) => Task.FromResult(Document(p));
        await model.LoadCellAsync(cell, "new.pdf");
        late.SetResult(Document("late.pdf"));
        await loading;
        Assert.AreEqual("new.pdf", cell.Content!.Document.FileName);
        foreach (bool switchMode in new[] { false, true })
        {
            late = new(); services.Load = (_, _) => late.Task;
            loading = model.LoadCellAsync(cell, "late.pdf");
            if (switchMode) { model.ModeIndex = 0; model.ModeIndex = 1; }
            else model.CancelOperationCommand.Execute(null);
            late.SetResult(Document("late.pdf"));
            await loading;
            Assert.AreEqual("new.pdf", cell.Content!.Document.FileName);
            Assert.IsFalse(model.HasPendingCellLoads);
        }
    }

    [TestMethod]
    public async Task Presets_KeepOldJsonCompatibleAndPersistOnlySettings_AndGuardDestructiveChanges()
    {
        using var files = new Fixture();
        string path = Path.Combine(files.DirectoryPath, "presets.json");
        var store = new JsonPdfLayOutPresetStore(path);
        await store.LoadAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["presets"]![0]!["settings"]!.AsObject().Remove("mode");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        Assert.AreEqual(PdfLayOutMode.Repeat, (await store.LoadAsync()).Single().Settings.Mode);
        var small = await store.AddAsync("small", Settings with { Columns = 1, Rows = 1 });
        var large = await store.AddAsync("large", Settings);
        var services = new Services();
        var model = new PdfLayOutViewModel(services, services, services, presetStore: store);
        await model.InitializePresetsAsync();
        model.SelectedPreset = model.Presets.Single(p => p.Id == large.Id);
        await model.LoadCellAsync(Cell(model, 1, 1), "secret-file.pdf");
        Assert.IsFalse(model.HasUnsavedPresetChanges);
        model.HorizontalGap = 6;
        Assert.IsFalse(model.TrySelectPreset(model.Presets.Single(p => p.Id == small.Id)));
        Assert.IsNull(model.PendingLayoutChange, "未保存配置保护应先于清空确认。");
        await model.SavePresetCommand.ExecuteAsync(null);
        Assert.IsFalse(model.TrySelectPreset(model.Presets.Single(p => p.Id == small.Id)));
        Assert.AreEqual(large.Id, model.SelectedPreset!.Id);
        await model.ResolveLayoutChangeAsync(model.PendingLayoutChange!, false);
        Assert.IsNotNull(Cell(model, 1, 1).Content);
        model.TrySelectPreset(model.Presets.Single(p => p.Id == small.Id));
        await model.ResolveLayoutChangeAsync(model.PendingLayoutChange!, true);
        Assert.AreEqual(small.Id, model.SelectedPreset!.Id);
        Assert.HasCount(1, model.FreeCells);
        string saved = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("secret-file", saved);
        Assert.DoesNotContain("sourcePageIndex", saved);
        Assert.DoesNotContain("zoom", saved);
        Assert.AreEqual(PdfLayOutMode.Free, (await store.LoadAsync()).Single(p => p.Id == large.Id).Settings.Mode);
    }

    [TestMethod]
    public async Task RefreshAndDeletePreset_ApplyOnlyAfterAcceptingRemoval()
    {
        using var files = new Fixture();
        var store = new JsonPdfLayOutPresetStore(Path.Combine(files.DirectoryPath, "presets.json"));
        await store.LoadAsync();
        await store.UpdateAsync(Guid.Empty, "默认配置", Settings with { Columns = 1, Rows = 1 });
        var large = await store.AddAsync("large", Settings);
        var services = new Services();
        var model = new PdfLayOutViewModel(services, services, services, presetStore: store);
        await model.InitializePresetsAsync();
        model.SelectedPreset = model.Presets.Single(p => p.Id == large.Id);
        await model.LoadCellAsync(Cell(model, 0, 0), "keep.pdf");
        await model.LoadCellAsync(Cell(model, 1, 1), "remove.pdf");
        var keep = Cell(model, 0, 0);
        await store.UpdateAsync(large.Id, large.Name, Settings with { Rows = 1 });
        await model.RefreshPresetsCommand.ExecuteAsync(null);
        Assert.AreEqual(2d, model.Rows);
        await model.ResolveLayoutChangeAsync(model.PendingLayoutChange!, false);
        Assert.IsNotNull(Cell(model, 1, 1).Content);
        await model.RefreshPresetsCommand.ExecuteAsync(null);
        await model.ResolveLayoutChangeAsync(model.PendingLayoutChange!, true);
        Assert.AreEqual(1d, model.Rows);
        Assert.AreSame(keep, Cell(model, 0, 0));
        model.Rows = 2;
        await model.SavePresetCommand.ExecuteAsync(null);
        await model.LoadCellAsync(Cell(model, 1, 1), "remove.pdf");
        await model.DeletePresetCommand.ExecuteAsync(null);
        Assert.IsNotNull(model.PendingLayoutChange);
        await model.ResolveLayoutChangeAsync(model.PendingLayoutChange!, false);
        Assert.IsTrue((await store.LoadAsync()).Any(p => p.Id == large.Id));
        await model.DeletePresetCommand.ExecuteAsync(null);
        await model.ResolveLayoutChangeAsync(model.PendingLayoutChange!, true);
        Assert.IsTrue(model.SelectedPreset!.IsDefault);
        Assert.HasCount(1, model.FreeCells);
        Assert.AreSame(keep, Cell(model, 0, 0));
        Assert.IsFalse((await store.LoadAsync()).Any(p => p.Id == large.Id));
    }

    [TestMethod]
    public async Task ShrinkingWhileLoading_CancelsRemovedCellWithoutChangingSurvivors()
    {
        var services = new Services();
        var model = Model(services);
        await model.LoadCellAsync(Cell(model, 0, 0), "keep.pdf");
        var late = new TaskCompletionSource<PdfLayOutDocument>();
        services.Load = (_, _) => late.Task;
        var removed = Cell(model, 1, 1);
        Task loading = model.LoadCellAsync(removed, "late.pdf");
        model.Rows = 1;
        Assert.IsNull(model.PendingLayoutChange);
        late.SetResult(Document("late.pdf"));
        await loading;
        Assert.IsFalse(model.HasPendingCellLoads);
        Assert.IsNull(removed.Content);
        Assert.AreEqual("keep.pdf", Cell(model, 0, 0).Content!.Document.FileName);
        model.Rows = 2;
        Assert.IsNull(Cell(model, 1, 1).Content);
    }

    [TestMethod]
    public async Task Previews_AreDeduplicatedBoundedToTwoAndEvictedAfterClearing()
    {
        var preview = new PreviewService();
        var services = new Services();
        var model = new PdfLayOutViewModel(services, services, services, preview) { ModeIndex = 1 };
        await model.LoadCellAsync(Cell(model, 0, 0), "one.pdf");
        await model.PreviewTask;
        int count = preview.Calls;
        model.CopyToCell(Cell(model, 0, 0).Content!, Cell(model, 0, 1));
        await model.PreviewTask;
        Assert.AreEqual(count, preview.Calls);
        Assert.AreSame(Cell(model, 0, 0).Preview, Cell(model, 0, 1).Preview);
        await model.LoadCellAsync(Cell(model, 1, 0), "two.pdf");
        await model.PreviewTask;
        await model.LoadCellAsync(Cell(model, 1, 1), "three.pdf");
        await model.PreviewTask;
        preview.Calls = 0; preview.Slow = true;
        await model.RefreshPreviewCommand.ExecuteAsync(null);
        Assert.AreEqual(3, preview.Calls);
        Assert.AreEqual(2, preview.Peak);
        var snapshot = Cell(model, 0, 0).Content!;
        model.ClearCell(Cell(model, 0, 0)); model.ClearCell(Cell(model, 0, 1));
        await model.PreviewTask;
        count = preview.Calls;
        model.CopyToCell(snapshot, Cell(model, 0, 1));
        await model.PreviewTask;
        Assert.AreEqual(count + 1, preview.Calls, "移除最后一个引用后应清理页面缓存。");
    }

    [TestMethod]
    [DataRow(PdfLayOutScaleMode.OriginalSize)]
    [DataRow(PdfLayOutScaleMode.Fit)]
    [DataRow(PdfLayOutScaleMode.Fill)]
    [DataRow(PdfLayOutScaleMode.Custom)]
    public async Task VectorExportAndMemoryPrint_UseOneSheetWithMixedPagesAndEmptyCells_AndOutlineFonts(PdfLayOutScaleMode mode)
    {
        using var files = new Fixture();
        var reader = new WindowsPdfLayOutDocumentService(128);
        var one = await reader.LoadAsync(files.CreatePdf("one.pdf", new(50, 30), XColors.Red, XColors.Blue), CancellationToken.None);
        var two = await reader.LoadAsync(files.CreatePdf("two.pdf", new(120, 200), XColors.Green, XColors.Black), CancellationToken.None);
        var settings = Settings with { ScaleMode = mode, ScalePercent = 170, Offset = new(8, -6), SourceCrop = new(2, 3, 4, 5) };
        var job = PdfLayOutJob.CreateFree([new(0, 0, one, 0), new(1, 0, one, 1), new(1, 1, two, 0)], settings);
        Assert.HasCount(2, job.Sources);
        Assert.HasCount(1, job.Sheets);
        var generator = new PdfSharpLayOutPdfGenerator();
        string target = Path.Combine(files.DirectoryPath, "result.pdf");
        var exporter = new PdfSharpLayOutExportService(generator);
        foreach (var source in job.Sources.Keys)
            await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(job, source, CancellationToken.None));
        await exporter.ExportAsync(job, target, CancellationToken.None);
        using (var pdf = PdfReader.Open(target, PdfDocumentOpenMode.Import))
        {
            Assert.AreEqual(1, pdf.PageCount);
            Assert.AreEqual(210d, pdf.Pages[0].Width.Millimeter, 0.001);
            Assert.AreEqual(297d, pdf.Pages[0].Height.Millimeter, 0.001);
            var objects = pdf.Internals.GetAllObjects().OfType<PdfDictionary>().ToArray();
            Assert.HasCount(3, objects.Where(o => o.Elements.GetName("/Subtype") == "/Form").ToArray());
            Assert.IsFalse(objects.Any(o => o.Elements.GetName("/Subtype") == "/Image"));
        }
        using (var input = File.OpenRead(target))
        using (var rasterizer = await WindowsPdfLayOutPrintRasterizer.OpenAsync(input, CancellationToken.None))
        {
            var bitmap = await rasterizer.RenderAsync(0, new(840, 1188), CancellationToken.None);
            foreach (var cell in job.Sheets[0].Cells)
            {
                var placed = job.Sheets[0].Placements.SingleOrDefault(p => p.Row == cell.Row && p.Column == cell.Column);
                // 按毫米位置采样整张印张：同时检查留白、位移和容器裁切。
                for (double x = cell.Bounds.X + 2; x < cell.Bounds.Right - 2; x += 9)
                    for (double y = cell.Bounds.Y + 2; y < cell.Bounds.Bottom - 2; y += 9)
                    {
                        if (placed is not null && new[] { Math.Abs(x - placed.ClipBounds.X), Math.Abs(x - placed.ClipBounds.Right),
                            Math.Abs(y - placed.ClipBounds.Y), Math.Abs(y - placed.ClipBounds.Bottom) }.Min() < 0.75) continue;
                        bool ink = placed is not null && x > placed.ClipBounds.X + 0.5 && x < placed.ClipBounds.Right - 0.5 &&
                            y > placed.ClipBounds.Y + 0.5 && y < placed.ClipBounds.Bottom - 0.5;
                        int pixel = ((int)(y * 4) * bitmap.Width + (int)(x * 4)) * 4;
                        var expected = !ink ? XColors.White : cell.Column == 1 ? XColors.Green : cell.Row == 0 ? XColors.Red : XColors.Blue;
                        Assert.AreEqual(expected.B, bitmap.BgraPixels[pixel], 8, $"{x},{y} blue");
                        Assert.AreEqual(expected.G, bitmap.BgraPixels[pixel + 1], 8, $"{x},{y} green");
                        Assert.AreEqual(expected.R, bitmap.BgraPixels[pixel + 2], 8, $"{x},{y} red");
                    }
            }
        }
        var backend = new PrintBackend();
        await new WindowsPdfLayOutPrintService(generator, backend).PrintAsync(job, CancellationToken.None);
        Assert.AreSame(job, backend.Job);
        using (var printed = PdfReader.Open(new MemoryStream(backend.Pdf!), PdfDocumentOpenMode.Import))
        using (var exported = PdfReader.Open(target, PdfDocumentOpenMode.Import))
            CollectionAssert.AreEqual(exported.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue,
                printed.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);
        var outlined = PdfLayOutJob.CreateFree([new(0, 0, one, 1), new(1, 1, two, 0)], settings,
            new() { OutlineFonts = true });
        await exporter.ExportAsync(outlined, target, CancellationToken.None);
        using var outlinedPdf = PdfReader.Open(target, PdfDocumentOpenMode.Import);
        Assert.AreEqual(1, outlinedPdf.PageCount);
        Assert.IsEmpty(PdfLayOutFontInspector.Inspect(outlinedPdf, CancellationToken.None));
        Assert.IsFalse(outlinedPdf.Internals.GetAllObjects().OfType<PdfDictionary>().Any(o => o.Elements.GetName("/Type") == "/Font"));
    }

    private sealed class Services : IPdfLayOutDocumentService, IPdfLayOutExportService, IPdfLayOutPrintService
    {
        public Func<string, CancellationToken, Task<PdfLayOutDocument>> Load { get; set; } = (p, _) => Task.FromResult(Document(p));
        public PdfLayOutJob? Job { get; private set; }
        public Task<PdfLayOutDocument> LoadAsync(string p, CancellationToken t) => Load(p, t);
        public Task ExportAsync(PdfLayOutJob job, string p, CancellationToken t) { Job = job; return Task.CompletedTask; }
        public Task<PdfLayOutPrintResult> PrintAsync(PdfLayOutJob job, CancellationToken t) { Job = job; return Task.FromResult(PdfLayOutPrintResult.Submitted); }
    }
    private sealed class PreviewService : IPdfLayOutPreviewService
    {
        public int Calls, Peak, Active;
        public bool Slow;
        public async Task<PdfLayOutPagePreview> RenderAsync(PdfLayOutDocument doc, int page, CancellationToken token)
        {
            Calls++; Active++; Peak = Math.Max(Peak, Active);
            try { if (Slow) await Task.Delay(50, token); return new(page, [1, 2], 1, 1); }
            finally { Active--; }
        }
    }
    private sealed class PrintBackend : IPdfLayOutPrintBackend
    {
        public byte[]? Pdf;
        public PdfLayOutJob? Job;
        public async Task<PdfLayOutPrintResult> PrintAsync(Stream stream, PdfLayOutJob job, CancellationToken token)
        {
            Assert.IsInstanceOfType<MemoryStream>(stream);
            Job = job; using var copy = new MemoryStream(); await stream.CopyToAsync(copy, token); Pdf = copy.ToArray();
            return PdfLayOutPrintResult.Submitted;
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "BigDvToolBox-Free-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(DirectoryPath);
        public string CreatePdf(string name, PdfLayOutSize size, params XColor[] colors)
        {
            string path = Path.Combine(DirectoryPath, name);
            using var pdf = new PdfDocument();
            foreach (var color in colors)
            {
                var page = pdf.AddPage(); page.Width = XUnit.FromMillimeter(size.Width); page.Height = XUnit.FromMillimeter(size.Height);
                using (var gfx = XGraphics.FromPdfPage(page)) gfx.DrawRectangle(new XSolidBrush(color), 0, 0, page.Width.Point, page.Height.Point);
                var font = new PdfDictionary(pdf); font.Elements.SetName("/Type", "/Font"); font.Elements.SetName("/Subtype", "/Type1"); font.Elements.SetName("/BaseFont", "/Helvetica"); pdf.Internals.AddObject(font);
                var fonts = new PdfDictionary(pdf); fonts.Elements["/F1"] = font.Reference!; page.Resources.Elements["/Font"] = fonts;
                page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("BT /F1 2 Tf 1 1 Td (VECTOR) Tj ET"));
            }
            pdf.Save(path); return path;
        }
        public void Dispose() { foreach (string path in Directory.GetFiles(DirectoryPath)) File.Delete(path); Directory.Delete(DirectoryPath); }
    }
}
