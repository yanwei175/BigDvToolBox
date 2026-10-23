using BigDvToolBox_App.Features.PdfOutlineFonts;
using BigDvToolBox_App.Services;

namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>负责文件路径校验和原子保存；拼版内容由共享的流生成器提供。</summary>
public sealed class PdfSharpLayOutExportService(IPdfLayOutPdfGenerator generator, IPdfFontOutliner? fontOutliner = null) : IPdfLayOutExportService
{
    private readonly IPdfFontOutliner _fontOutliner = fontOutliner ?? new GhostscriptService();
    public PdfSharpLayOutExportService() : this(new PdfSharpLayOutPdfGenerator()) { }

    public async Task ExportAsync(PdfLayOutJob job, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string target = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(target), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("导出文件必须使用 .pdf 扩展名。", nameof(outputPath));
        if (job.Sources.Keys.Any(path => string.Equals(target, path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("导出路径不能与源 PDF 相同。");

        string temporaryPath = Path.Combine(Path.GetDirectoryName(target)!, $".imposer-{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (job.ExportOptions.OutlineFonts)
                {
                    // 中间的拼版 PDF 只存在内存中；最后一步仍原子替换目标文件。
                    using var composed = new MemoryStream();
                    await generator.GenerateAsync(job, composed, cancellationToken).ConfigureAwait(false);
                    composed.Position = 0;
                    await _fontOutliner.OutlineAsync(composed, temporary, cancellationToken).ConfigureAwait(false);
                }
                else
                    await generator.GenerateAsync(job, temporary, cancellationToken).ConfigureAwait(false);
                await temporary.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

