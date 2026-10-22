using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Globalization;
using System.Text;

namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>仅修改导入文档的内存副本，将 PDF 原生坐标规范为版式使用的旋转后可见页面。</summary>
internal static class PdfLayOutSourcePageNormalizer
{
    public static void Normalize(PdfPage page, PdfLayOutSourcePage expected)
    {
        PdfRectangle media = page.MediaBox;
        PdfRectangle crop = page.EffectiveCropBoxReadOnly;
        double left = Math.Max(media.X1, crop.X1), bottom = Math.Max(media.Y1, crop.Y1);
        double right = Math.Min(media.X2, crop.X2), top = Math.Min(media.Y2, crop.Y2);
        double unit = page.Elements.ContainsKey("/UserUnit") ? page.Elements.GetReal("/UserUnit") : 1;
        int rotation = (page.Rotate % 360 + 360) % 360;
        if (rotation % 90 != 0 || !double.IsFinite(unit) || unit <= 0 || right <= left || top <= bottom)
            throw new InvalidDataException($"第 {expected.PageNumber} 页的页面边界或旋转无效。");

        double width = (rotation is 90 or 270 ? top - bottom : right - left) * unit;
        double height = (rotation is 90 or 270 ? right - left : top - bottom) * unit;
        if (!double.IsFinite(width) || !double.IsFinite(height) ||
            Math.Abs(width * 25.4 / 72 - expected.Size.Width) > 0.01 ||
            Math.Abs(height * 25.4 / 72 - expected.Size.Height) > 0.01)
            throw new InvalidDataException($"第 {expected.PageNumber} 页的尺寸已发生变化或不受支持，请重新加载文件。");

        // PDF 原始坐标以左下角为原点。下面的矩阵同时应用 CropBox、顺时针旋转和 UserUnit。
        // 规范后的页面仍使用 PDF 坐标；XGraphics 在输出时负责转换到左上角向下的版式坐标。
        (double a, double b, double c, double d, double e, double f) = rotation switch
        {
            0 => (1, 0, 0, 1, -left, -bottom),
            90 => (0, -1, 1, 0, -bottom, right),
            180 => (-1, 0, 0, -1, right, top),
            _ => (0, 1, -1, 0, top, -left),
        };
        // PDF 数字不支持科学计数法。
        string prefix = "q\n" + string.Join(" ", new[] { a, b, c, d, e, f }
            .Select(value => (value * unit).ToString("0.############################", CultureInfo.InvariantCulture))) + " cm\n";
        page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes(prefix));
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("\nQ\n"));
        page.Rotate = 0;
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(width, height));
        page.CropBox = page.MediaBox;
        page.Elements.Remove("/UserUnit");
    }
}
