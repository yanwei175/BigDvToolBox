using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BigDvToolBox_App.Features.PdfLayOut;

internal interface IPdfLayOutPrintDiagnosticsProvider
{
    string? LastDiagnosticPath { get; }
}

/// <summary>每次打印一份 JSON Lines 日志。逐行落盘；诊断失败不能改变打印结果。</summary>
internal sealed class PdfLayOutPrintDiagnostics : IDisposable
{
    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BigDvToolBox", "Logs", "Printing");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private StreamWriter? _writer;
    private long _stageId;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string? FilePath { get; private set; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public string TaskName { get; }
    public bool HasSystemTask { get; set; }

    public PdfLayOutPrintDiagnostics(PdfLayOutJob job, string? directory = null)
    {
        TaskName = $"{job.DisplayName} [{Id[..8]}]";
        try
        {
            string folder = Path.GetFullPath(directory ?? DefaultDirectory);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"print-{StartedAt:yyyyMMdd-HHmmss-fff}-{Id[..8]}.log");
            _writer = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            FilePath = path;
        }
        catch (Exception exception) { Debug.WriteLine($"无法创建打印日志：{exception}"); }
        Record("Print.Start", new
        {
            taskName = TaskName, appVersion = typeof(PdfLayOutPrintDiagnostics).Assembly.GetName().Version?.ToString(),
            os = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            mode = job.Settings.Mode.ToString(), requestedSheetMm = job.Settings.SheetSize, sheetCount = job.Sheets.Count,
            sourceCount = job.Sources.Count, placementCount = job.Sheets.Sum(s => s.Placements.Count),
            maximumRasterDpi = PdfLayOutPrintGeometry.MaximumRasterDpi, maximumRasterPixels = PdfLayOutPrintGeometry.MaximumRasterPixels,
            note = "Submitted 只表示已提交到 Windows，不代表打印机已出纸。像素字节数不是网络传输量。",
        });
        Capture("Environment.Windows", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return new { displayVersion = key?.GetValue("DisplayVersion"), build = key?.GetValue("CurrentBuildNumber"), ubr = key?.GetValue("UBR") };
        });
    }

    public void Record(string name, object? data = null)
    {
        lock (_gate)
        {
            if (_writer is null) return;
            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(new
                {
                    time = DateTimeOffset.Now, elapsedMs = Math.Round(_clock.Elapsed.TotalMilliseconds, 3),
                    threadId = Environment.CurrentManagedThreadId, @event = name, data,
                }, JsonOptions));
            }
            catch (Exception exception) { Debug.WriteLine($"无法写入打印日志：{exception}"); }
        }
    }

    public void Error(string stage, Exception exception) => Record(stage + ".Error", new
    {
        type = exception.GetType().FullName, hResult = $"0x{exception.HResult:X8}", exception.Message, details = exception.ToString(),
    });

    public void Capture(string name, Func<object> capture)
    {
        try { Record(name, capture()); }
        catch (Exception exception) { Error(name + ".DiagnosticRead", exception); }
    }

    public IDisposable Stage(string name, object? data = null)
    {
        long stageId = Interlocked.Increment(ref _stageId);
        Record(name + ".Begin", new { stageId, context = data });
        return new TimedStage(this, name, stageId);
    }

    private sealed class TimedStage(PdfLayOutPrintDiagnostics log, string name, long stageId) : IDisposable
    {
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        public void Dispose() => log.Record(name + ".End", new { stageId, durationMs = Math.Round(_timer.Elapsed.TotalMilliseconds, 3) });
    }

    public void RecordPage(string phase, int page, PdfLayOutSize requested, double paperWidthDip, double paperHeightDip,
        PdfLayOutRect imageableDip, uint dpiX, uint dpiY, PdfLayOutPrintRasterSize pixels)
    {
        var paperMm = new PdfLayOutSize(paperWidthDip * 25.4 / 96, paperHeightDip * 25.4 / 96);
        Record("Page.Description", new
        {
            phase, page = page + 1, requestedSheetMm = requested,
            driverPaperDip = new PdfLayOutSize(paperWidthDip, paperHeightDip), driverPaperMm = paperMm,
            imageableDip, imageableMm = new PdfLayOutRect(imageableDip.X * 25.4 / 96, imageableDip.Y * 25.4 / 96,
                imageableDip.Width * 25.4 / 96, imageableDip.Height * 25.4 / 96),
            driverDpiX = dpiX, driverDpiY = dpiY,
            paperSizeMismatch = Math.Abs(requested.Width - paperMm.Width) > 0.5 || Math.Abs(requested.Height - paperMm.Height) > 0.5,
            bitmapWidth = pixels.Width, bitmapHeight = pixels.Height, pixelFormat = "BGRA8",
            rawBytes = (long)pixels.Width * pixels.Height * 4,
            effectiveRasterDpiX = pixels.Width * 25.4 / requested.Width, effectiveRasterDpiY = pixels.Height * 25.4 / requested.Height,
        });
    }

    // 系统队列可能在 Submitted 之后才失败。观察一分钟，只记录带本任务唯一名称的错误事件。
    public async Task ObserveSystemErrorsAsync()
    {
        try
        {
            var seen = new HashSet<string>();
            Record("WindowsPrintEvents.Observe", new { seconds = 60, taskName = TaskName });
            for (int attempt = 0; attempt <= 6; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                foreach (string xml in await Task.Run(() => WindowsPrintErrorEvents.Read(StartedAt, TaskName)).ConfigureAwait(false))
                    if (seen.Add(xml)) Record("WindowsPrintEvents.MatchingError", new { xml });
            }
            Record("WindowsPrintEvents.End", new { matchingErrors = seen.Count });
        }
        catch (Exception exception) { Error("WindowsPrintEvents", exception); }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); }
            catch (Exception exception) { Debug.WriteLine(exception); }
            _writer = null;
        }
    }
}
