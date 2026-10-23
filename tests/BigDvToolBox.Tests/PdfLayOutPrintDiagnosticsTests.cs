using BigDvToolBox_App.Features.PdfLayOut;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutPrintDiagnosticsTests
{
    private static PdfLayOutJob Job() => new(new PdfLayOutDocument("input.pdf", [new(0, new(100, 100))]), new()
    { GridMode = PdfLayOutGridMode.Manual, Columns = 1, Rows = 1, Marks = new() { ShowCropMarks = false } });

    [TestMethod]
    public async Task Success_LogsSharedSessionPdfSizePaperDpiAndTimings_WithoutARealPrinter()
    {
        using var files = new Fixture();
        var backend = new Backend((_, job, log, _) =>
        {
            log.RecordPage("Final", 0, job.Settings.SheetSize, 148 * 96 / 25.4, 210 * 96 / 25.4,
                new(5, 5, 540, 780), 600, 1200, new(4000, 5660));
            return Task.FromResult(PdfLayOutPrintResult.Submitted);
        });
        var service = new WindowsPdfLayOutPrintService(new Generator(), backend, job => new(job, files.DirectoryPath));
        Assert.AreEqual(PdfLayOutPrintResult.Submitted, await service.PrintAsync(Job(), CancellationToken.None));
        Assert.AreEqual(1, Directory.GetFiles(files.DirectoryPath).Length, "只生成日志，不写临时 PDF。");
        Assert.IsNotNull(backend.Stream);
        Assert.IsFalse(backend.Stream.CanRead);
        var events = files.Read(service.LastDiagnosticPath!);
        Assert.IsTrue(events.Any(e => e.GetProperty("event").GetString() == "Print.Start"));
        Assert.AreEqual(3, Event(events, "GeneratePdf.Result").GetProperty("data").GetProperty("bytes").GetInt32());
        var page = Event(events, "Page.Description").GetProperty("data");
        Assert.AreEqual(148d, page.GetProperty("driverPaperMm").GetProperty("width").GetDouble(), 0.001);
        Assert.AreEqual(210d, page.GetProperty("requestedSheetMm").GetProperty("width").GetDouble());
        Assert.IsTrue(page.GetProperty("paperSizeMismatch").GetBoolean());
        Assert.AreEqual(600, page.GetProperty("driverDpiX").GetInt32());
        Assert.AreEqual(1200, page.GetProperty("driverDpiY").GetInt32());
        Assert.AreEqual(90_560_000L, page.GetProperty("rawBytes").GetInt64());
        Assert.AreEqual("Submitted", Event(events, "Print.Result").GetProperty("data").GetProperty("result").GetString());
        var begin = Event(events, "GeneratePdf.Begin").GetProperty("data");
        var end = Event(events, "GeneratePdf.End").GetProperty("data");
        Assert.AreEqual(begin.GetProperty("stageId").GetInt64(), end.GetProperty("stageId").GetInt64());
        Assert.IsTrue(end.GetProperty("durationMs").GetDouble() >= 0);
    }

    [TestMethod]
    public async Task Failure_PreservesConcreteHResultAndInnerError_AndDisplaysLogLocation()
    {
        using var files = new Fixture();
        var backend = new Backend((_, _, _, _) => throw new IOException("提交阶段失败", new COMException("无法发送任务", unchecked((int)0x80070035))));
        var service = new WindowsPdfLayOutPrintService(new Generator(), backend, job => new(job, files.DirectoryPath));
        var model = new PdfLayOutViewModel(null, null, service);
        model.SetSourceDocument(Job().Document);
        await model.PrintCommand.ExecuteAsync(null);
        Assert.IsFalse(model.IsBusy);
        Assert.IsNotNull(model.ErrorMessage);
        StringAssert.Contains(model.StatusMessage, service.LastDiagnosticPath!);
        var error = Event(files.Read(service.LastDiagnosticPath!), "Print.Error").GetProperty("data");
        Assert.AreEqual(typeof(IOException).FullName, error.GetProperty("type").GetString());
        StringAssert.Contains(error.GetProperty("hResult").GetString()!, "0x");
        StringAssert.Contains(error.GetProperty("details").GetString()!, "0x80070035");
        StringAssert.Contains(error.GetProperty("details").GetString()!, "无法发送任务");
        Assert.IsFalse(backend.Stream!.CanRead);
    }

    [TestMethod]
    public async Task Cancellation_KeepsFlushedPartialLogAndDoesNotReportAnError()
    {
        using var files = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new Generator(async (stream, token) =>
        {
            await stream.WriteAsync(new byte[] { 1 }, token);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        var service = new WindowsPdfLayOutPrintService(generator,
            new Backend((_, _, _, _) => throw new AssertFailedException("取消生成后不能启动打印。")), job => new(job, files.DirectoryPath));
        using var cancellation = new CancellationTokenSource();
        Task printing = service.PrintAsync(Job(), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(files.Read(service.LastDiagnosticPath!).Any(e => e.GetProperty("event").GetString() == "GeneratePdf.Begin"), "未结束时已落盘。");
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => printing);
        var events = files.Read(service.LastDiagnosticPath!);
        Assert.IsTrue(events.Any(e => e.GetProperty("event").GetString() == "Print.Cancelled"));
        Assert.IsFalse(events.Any(e => e.GetProperty("event").GetString() == "Print.Error"));
    }

    [TestMethod]
    public async Task LogDirectoryFailure_DoesNotPreventPrinting()
    {
        using var files = new Fixture();
        string blocked = Path.Combine(files.DirectoryPath, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "file");
        var service = new WindowsPdfLayOutPrintService(new Generator(),
            new Backend((_, _, _, _) => Task.FromResult(PdfLayOutPrintResult.Submitted)), job => new(job, blocked));
        Assert.AreEqual(PdfLayOutPrintResult.Submitted, await service.PrintAsync(Job(), CancellationToken.None));
        Assert.IsNull(service.LastDiagnosticPath);
        Assert.AreEqual("file", await File.ReadAllTextAsync(blocked));
    }

    [TestMethod]
    public async Task NativeRasterizer_LogsEncodedPngAndActualBgraBytes()
    {
        using var files = new Fixture();
        using var log = new PdfLayOutPrintDiagnostics(Job(), files.DirectoryPath);
        using var pdf = new PdfDocument();
        var page = pdf.AddPage(); page.Width = XUnit.FromMillimeter(210); page.Height = XUnit.FromMillimeter(297);
        using (var graphics = XGraphics.FromPdfPage(page)) graphics.DrawRectangle(XBrushes.Black, 10, 10, 100, 200);
        using var stream = new MemoryStream(); pdf.Save(stream, closeStream: false); stream.Position = 0;
        using var renderer = await WindowsPdfLayOutPrintRasterizer.OpenAsync(stream, CancellationToken.None);
        var pixels = await renderer.RenderAsync(0, new(420, 594), CancellationToken.None, log, "Final");
        var events = files.Read(log.FilePath!);
        Assert.AreEqual(pixels.BgraPixels.LongLength, Event(events, "Raster.Bitmap").GetProperty("data").GetProperty("rawBytes").GetInt64());
        Assert.IsTrue(Event(events, "Raster.Png").GetProperty("data").GetProperty("bytes").GetInt64() > 0);
        foreach (string stage in new[] { "PdfPage.RenderToPng", "Png.OpenDecoder", "Png.DecodeBgra" })
            Assert.IsTrue(Event(events, stage + ".End").GetProperty("data").GetProperty("durationMs").GetDouble() >= 0);
    }

    [TestMethod]
    public void ConcurrentWrites_ProduceCompleteJsonLines_AndEventMatchingIsTaskSpecific()
    {
        using var files = new Fixture();
        using var log = new PdfLayOutPrintDiagnostics(Job(), files.DirectoryPath);
        Parallel.For(0, 200, i => log.Record("Parallel", new { number = i }));
        Assert.AreEqual(200, files.Read(log.FilePath!).Count(e => e.GetProperty("event").GetString() == "Parallel"));
        const string name = "Label <one> [12345678]";
        Assert.IsTrue(WindowsPrintErrorEvents.MatchesTask("<Event><Data>Label &lt;one&gt; [12345678]</Data></Event>", name));
        Assert.IsFalse(WindowsPrintErrorEvents.MatchesTask("<Event><Data>Label &lt;one&gt; [87654321]</Data></Event>", name));
    }

    [TestMethod]
    public void NativeEventQuery_CanReadWithoutChangingPrinters()
    {
        try { Assert.IsEmpty(WindowsPrintErrorEvents.Read(DateTimeOffset.Now.AddMinutes(-1), "BigDvToolBox-" + Guid.NewGuid())); }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 5 or 15007)
        { /* 无权限或未安装此事件通道时由诊断层记录读取错误；不需要提权来打印。 */ }
    }

    private static JsonElement Event(JsonElement[] events, string name) => events.First(e => e.GetProperty("event").GetString() == name);
    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "BigDvToolBox-PrintDiagnostics-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(DirectoryPath);
        public JsonElement[] Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => { using var json = JsonDocument.Parse(line); return json.RootElement.Clone(); }).ToArray();
        }
        public void Dispose() { foreach (string path in Directory.GetFiles(DirectoryPath)) File.Delete(path); Directory.Delete(DirectoryPath); }
    }
    private sealed class Generator(Func<Stream, CancellationToken, Task>? generate = null) : IPdfLayOutPdfGenerator
    {
        public Task GenerateAsync(PdfLayOutJob job, Stream stream, CancellationToken token) => generate?.Invoke(stream, token) ?? stream.WriteAsync(new byte[] { 1, 2, 3 }, token).AsTask();
    }
    private sealed class Backend(Func<Stream, PdfLayOutJob, PdfLayOutPrintDiagnostics, CancellationToken, Task<PdfLayOutPrintResult>> print) : IPdfLayOutPrintBackend
    {
        public Stream? Stream;
        public Task<PdfLayOutPrintResult> PrintAsync(Stream stream, PdfLayOutJob job, CancellationToken token) => throw new AssertFailedException("应该传入同一诊断会话。");
        public Task<PdfLayOutPrintResult> PrintAsync(Stream stream, PdfLayOutJob job, PdfLayOutPrintDiagnostics log, CancellationToken token)
        { Stream = stream; return print(stream, job, log, token); }
    }
}
