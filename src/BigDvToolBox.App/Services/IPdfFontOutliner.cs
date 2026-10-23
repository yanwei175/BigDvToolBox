namespace BigDvToolBox_App.Services;

/// <summary>将输入流当前位置开始的 PDF 转曲写入输出流；调用方拥有两个流。</summary>
public interface IPdfFontOutliner
{
    Task OutlineAsync(Stream input, Stream output, CancellationToken cancellationToken);
}
