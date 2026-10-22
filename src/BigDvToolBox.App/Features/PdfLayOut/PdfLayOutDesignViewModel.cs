namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>仅供 XAML 设计器和界面预览使用，示例数据不进入正式运行时 ViewModel。</summary>
public sealed class PdfLayOutDesignViewModel : PdfLayOutViewModel
{
    public PdfLayOutDesignViewModel()
    {
        SetSourceDocument(new PdfLayOutDocument("sample.pdf",
            Enumerable.Range(0, 8).Select(index => new PdfLayOutSourcePage(index, new(148, 210)))));
    }
}
