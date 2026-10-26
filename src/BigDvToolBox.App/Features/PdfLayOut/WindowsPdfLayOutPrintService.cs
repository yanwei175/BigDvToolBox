namespace BigDvToolBox_App.Features.PdfLayOut;

internal interface IPdfLayOutPrintBackend
{
    Task<PdfLayOutPrintResult> PrintAsync(Stream imposedPdf, PdfLayOutJob job, CancellationToken cancellationToken);
    Task<PdfLayOutPrintResult> PrintAsync(Stream imposedPdf, PdfLayOutJob job, PdfLayOutPrintDiagnostics diagnostics,
        CancellationToken cancellationToken) => PrintAsync(imposedPdf, job, cancellationToken);
}

/// <summary>每次按当前版式在内存中生成 PDF；流一直保留到预览和打印结束。</summary>
public sealed class WindowsPdfLayOutPrintService : IPdfLayOutPrintService, IPdfLayOutPrintDiagnosticsProvider
{
    private readonly IPdfLayOutPdfGenerator _generator;
    private readonly IPdfLayOutPrintBackend _backend;
    private readonly Func<PdfLayOutJob, PdfLayOutPrintDiagnostics> _createDiagnostics;
    public string? LastDiagnosticPath { get; private set; }

    internal WindowsPdfLayOutPrintService(IPdfLayOutPdfGenerator generator, IPdfLayOutPrintBackend backend,
        Func<PdfLayOutJob, PdfLayOutPrintDiagnostics>? createDiagnostics = null)
    {
        _generator = generator;
        _backend = backend;
        _createDiagnostics = createDiagnostics ?? (job => new(job));
    }

    public async Task<PdfLayOutPrintResult> PrintAsync(PdfLayOutJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var diagnostics = _createDiagnostics(job);
        LastDiagnosticPath = diagnostics.FilePath;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pdf = new MemoryStream();
            using (diagnostics.Stage("GeneratePdf"))
                await _generator.GenerateAsync(job, pdf, cancellationToken);
            diagnostics.Record("GeneratePdf.Result", new { bytes = pdf.Length });
            cancellationToken.ThrowIfCancellationRequested();
            pdf.Position = 0;
            PdfLayOutPrintResult result;
            using (diagnostics.Stage("PrintBackend"))
                result = await _backend.PrintAsync(pdf, job, diagnostics, cancellationToken);
            diagnostics.Record("Print.Result", new { result = result.ToString() });
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { diagnostics.Record("Print.Cancelled"); throw; }
        catch (Exception exception) { diagnostics.Error("Print", exception); throw; }
        finally
        {
            diagnostics.Record("Print.End");
            if (diagnostics.IsEnabled && diagnostics.HasSystemTask) _ = diagnostics.ObserveSystemErrorsAsync();
            else diagnostics.Dispose();
        }
    }
}
