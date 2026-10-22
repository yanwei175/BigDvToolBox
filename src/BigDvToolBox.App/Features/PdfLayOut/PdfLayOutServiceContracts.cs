namespace BigDvToolBox_App.Features.PdfLayOut;

// PDF 元数据和图像渲染与打印、导出分离，避免预览质量影响矢量输出。
public interface IPdfLayOutDocumentService
{
    Task<PdfLayOutDocument> LoadAsync(string filePath, CancellationToken cancellationToken);
}

public interface IPdfLayOutPreviewService
{
    Task<PdfLayOutPagePreview> RenderAsync(PdfLayOutDocument document, int pageIndex, CancellationToken cancellationToken);
}

public interface IPdfLayOutExportService
{
    // 直接绘制 job.Sheets 中的 ImageBounds/ClipBounds 和 Marks，无需再次推算版式。
    Task ExportAsync(PdfLayOutJob job, string outputPath, CancellationToken cancellationToken);
}

/// <summary>只生成拼版内容；调用方拥有输出流，决定保存到文件还是留在内存中打印。</summary>
public interface IPdfLayOutPdfGenerator
{
    Task GenerateAsync(PdfLayOutJob job, Stream output, CancellationToken cancellationToken);
}

public interface IPdfLayOutPrintService
{
    // Submitted 仅表示已提交到打印系统，不表示纸张已经完成打印。
    Task<PdfLayOutPrintResult> PrintAsync(PdfLayOutJob job, CancellationToken cancellationToken);
}
