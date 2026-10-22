namespace BigDvToolBox_App.Features.PdfLayOut;

internal interface IPdfLayOutPrintBackend
{
    Task<PdfLayOutPrintResult> PrintAsync(Stream imposedPdf, PdfLayOutJob job, CancellationToken cancellationToken);
}

/// <summary>每次按当前版式在内存中生成 PDF；流一直保留到预览和打印结束。</summary>
public sealed class WindowsPdfLayOutPrintService : IPdfLayOutPrintService
{
    private readonly IPdfLayOutPdfGenerator _generator;
    private readonly IPdfLayOutPrintBackend _backend;

    internal WindowsPdfLayOutPrintService(IPdfLayOutPdfGenerator generator, IPdfLayOutPrintBackend backend)
    {
        _generator = generator;
        _backend = backend;
    }

    public async Task<PdfLayOutPrintResult> PrintAsync(PdfLayOutJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        using var pdf = new MemoryStream();
        await _generator.GenerateAsync(job, pdf, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        pdf.Position = 0;
        return await _backend.PrintAsync(pdf, job, cancellationToken);
    }
}
