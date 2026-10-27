using System.Diagnostics.CodeAnalysis;

namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>
/// 单次打印会话的纸面几何。只复制印张尺寸，不持有或修改版式配置。
/// 打印设置变化后使旧读取失效；每次按驱动返回的整纸尺寸和可打印区域重新生成。
/// </summary>
internal sealed class PdfLayOutPrintSessionGeometry(IEnumerable<PdfLayOutSize> sheetSizes)
{
    private readonly PdfLayOutSize[] _sheets = sheetSizes.ToArray();
    public long Revision { get; private set; }

    public void Invalidate() => Revision++;

    public bool TryCreatePage(long revision, int index, PdfLayOutSize paperInDip, PdfLayOutRect imageableInDip,
        [NotNullWhen(true)] out PdfLayOutPrintVisualPlan? plan)
    {
        plan = null;
        if (revision != Revision) return false;
        // WinUI PrintDocument 的原点是整纸左上角。硬件边距只裁切，不叠加到版式边距上。
        plan = PdfLayOutPrintGeometry.CreateVisualPlan(_sheets[index], paperInDip, imageableInDip);
        return true;
    }
}
