using BigDvToolBox_App.Features.PdfLayOut;
using PdfSharp.Drawing;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutModelTests
{
    [TestMethod]
    public void PdfsharpPackage_CreatesA4DocumentInMemory()
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        var page = document.AddPage();
        page.Width = XUnit.FromMillimeter(210);
        page.Height = XUnit.FromMillimeter(297);
        using var stream = new MemoryStream();
        document.Save(stream);

        Assert.AreEqual(210d, page.Width.Millimeter, 0.000001);
        StringAssert.StartsWith(System.Text.Encoding.ASCII.GetString(stream.ToArray()), "%PDF-");
    }

    [TestMethod]
    public void SourceMetadata_CopiesTheSuppliedPageCollection()
    {
        var pages = new List<PdfLayOutSourcePage> { new(0, new(100, 50)) };
        var document = new PdfLayOutDocument("source.pdf", pages);
        pages.Clear();
        Assert.AreEqual(1, document.PageCount);
    }

    [TestMethod]
    public void ManualLayout_UsesMillimeterMarginsGapsAndOffsets()
    {
        var result = PdfLayOutCalculator.Calculate(new(0, new(100, 50)), new()
        {
            GridMode = PdfLayOutGridMode.Manual,
            SheetMargins = new(10, 20, 10, 20),
            HorizontalGap = 10, VerticalGap = 5,
            Offset = new(2, -3),
        });

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(4, result.Placements.Count);
        Assert.AreEqual(new PdfLayOutRect(10, 20, 90, 126), result.Placements[0].CellBounds);
        Assert.AreEqual(110d, result.Placements[1].CellBounds.X);
        Assert.AreEqual(151d, result.Placements[2].CellBounds.Y);
        Assert.AreEqual(new PdfLayOutRect(7, 55, 100, 50), result.Placements[0].ContentBounds);
    }

    [TestMethod]
    [DataRow(PdfLayOutScaleMode.OriginalSize, 100d, 50d)]
    [DataRow(PdfLayOutScaleMode.Fit, 90d, 45d)]
    [DataRow(PdfLayOutScaleMode.Fill, 252d, 126d)]
    [DataRow(PdfLayOutScaleMode.Custom, 200d, 100d)]
    public void ScaleMode_ProducesExpectedContentBounds(PdfLayOutScaleMode mode, double width, double height)
    {
        var result = PdfLayOutCalculator.Calculate(new(0, new(100, 50)), new()
        {
            GridMode = PdfLayOutGridMode.Manual,
            SheetMargins = new(10, 20, 10, 20),
            HorizontalGap = 10, VerticalGap = 5,
            ScaleMode = mode, ScalePercent = 200,
        });

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(width, result.Placements[0].ContentBounds.Width, 0.000001);
        Assert.AreEqual(height, result.Placements[0].ContentBounds.Height, 0.000001);
    }

    [TestMethod]
    public void AutomaticLayout_UsesCroppedDimensionsAndKeepsContainersFixedDuringScaling()
    {
        var settings = new PdfLayOutSettings { SourceCrop = new(10, 10, 10, 10) };
        var page = new PdfLayOutSourcePage(2, new(120, 120));
        var cropped = PdfLayOutCalculator.Calculate(page, settings);
        Assert.AreEqual(2, cropped.Columns);
        Assert.AreEqual(new PdfLayOutRect(10, 10, 100, 100), cropped.Placements[0].SourceBounds);
        Assert.IsTrue(cropped.Placements.All(cell => cell.SourcePageIndex == 2));

        var scaled = PdfLayOutCalculator.Calculate(page, settings with { ScaleMode = PdfLayOutScaleMode.Custom, ScalePercent = 200 });
        Assert.AreEqual(cropped.Columns, scaled.Columns);
        Assert.AreEqual(cropped.Rows, scaled.Rows);
        Assert.AreEqual(cropped.Placements[0].CellBounds, scaled.Placements[0].CellBounds);
        Assert.AreEqual(2d, scaled.Placements[0].Scale);
    }

    [TestMethod]
    public void InvalidGeometry_DoesNotCreateExportablePlacements()
    {
        var page = new PdfLayOutSourcePage(0, new(100, 100));
        PdfLayOutSettings[] invalidSettings =
        [
            new() { SheetSize = new(double.NaN, 297) },
            new() { SheetMargins = new(200, 0, 20, 0) },
            new() { SourceCrop = new(60, 0, 60, 0) },
            new() { GridMode = PdfLayOutGridMode.Manual, HorizontalGap = 300 },
            new() { VerticalGap = -1 },
        ];
        foreach (PdfLayOutSettings settings in invalidSettings)
        {
            var result = PdfLayOutCalculator.Calculate(page, settings);
            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(0, result.Placements.Count);
            Assert.IsNotNull(result.ErrorMessage);
        }
    }
}
