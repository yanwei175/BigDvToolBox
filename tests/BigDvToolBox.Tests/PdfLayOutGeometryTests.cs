using BigDvToolBox_App.Features.PdfLayOut;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutGeometryTests
{
    private static PdfLayOutSettings SingleCell => new()
    {
        SheetSize = new(200, 200), SheetMargins = new(10, 10, 10, 10),
        GridMode = PdfLayOutGridMode.Manual, Columns = 1, Rows = 1,
        SourceCrop = new(10, 20, 30, 10),
    };

    [TestMethod]
    [DataRow(PdfLayOutScaleMode.OriginalSize, 1d, 60d, 55d, 100d, 80d, 70d, 75d, 60d, 50d)]
    [DataRow(PdfLayOutScaleMode.Fit, 3d, -20d, -35d, 300d, 240d, 10d, 25d, 180d, 150d)]
    [DataRow(PdfLayOutScaleMode.Fill, 3.6d, -44d, -62d, 360d, 288d, 10d, 10d, 180d, 180d)]
    [DataRow(PdfLayOutScaleMode.Custom, 1.5d, 40d, 32.5d, 150d, 120d, 55d, 62.5d, 90d, 75d)]
    public void CroppedPage_ResolvesWholeImageAndFinalClipForEveryScaleMode(
        PdfLayOutScaleMode mode, double scale, double x, double y, double width, double height,
        double clipX, double clipY, double clipWidth, double clipHeight)
    {
        var layout = PdfLayOutCalculator.Calculate(new(0, new(100, 80)), SingleCell with { ScaleMode = mode, ScalePercent = 150 });
        PdfLayOutPlacement cell = layout.Placements.Single();

        Assert.AreEqual(scale, cell.Scale, 0.000001);
        AssertRect(new(x, y, width, height), cell.ImageBounds);
        AssertRect(new(clipX, clipY, clipWidth, clipHeight), cell.ClipBounds);
        Assert.AreEqual(new PdfLayOutRect(10, 20, 60, 50), cell.SourceBounds);
        // 裁掉的页边不能因为单元格有留白而重新出现在预览/导出中。
        AssertRect(cell.ContentBounds.Intersect(cell.CellBounds), cell.TrimBounds);
        AssertRect(cell.TrimBounds.Intersect(layout.SheetBounds), cell.ClipBounds);
    }

    [TestMethod]
    public void Offset_MovesOnlyLabelAndClipsOverflowToFixedContainer()
    {
        var settings = SingleCell with { SheetSize = new(100, 100), SourceCrop = default, ScaleMode = PdfLayOutScaleMode.Fit, Offset = new(-30, 25) };
        var layout = PdfLayOutCalculator.Calculate(new(0, new(100, 100)), settings);

        Assert.AreEqual(new PdfLayOutRect(10, 10, 80, 80), layout.GridBounds);
        Assert.AreEqual(layout.GridBounds, layout.Placements.Single().CellBounds);
        Assert.AreEqual(new PdfLayOutRect(-20, 35, 80, 80), layout.Placements.Single().ImageBounds);
        Assert.AreEqual(new PdfLayOutRect(10, 35, 50, 55), layout.Placements.Single().ClipBounds);

        var offPaper = PdfLayOutCalculator.Calculate(new(0, new(100, 100)), settings with { Offset = new(500, 500) });
        Assert.IsTrue(offPaper.Placements.Single().ClipBounds.IsEmpty);
        CollectionAssert.AreEquivalent(layout.Marks.Single().Lines.ToArray(), offPaper.Marks.Single().Lines.ToArray(),
            "标签完全移出容器也不应影响容器裁切标记。");
    }

    [TestMethod]
    public void A4_ArrangesTenFixedContainersWithFiveMillimeterGaps()
    {
        var layout = PdfLayOutCalculator.Calculate(new(0, new(95, 52)), new());
        Assert.AreEqual(2, layout.Columns);
        Assert.AreEqual(5, layout.Rows);
        AssertRect(new(0, 0, 102.5, 55.4), layout.Placements[0].CellBounds);
        AssertRect(new(3.75, 1.7, 95, 52), layout.Placements[0].ContentBounds);
        Assert.AreEqual(5, layout.Placements[1].CellBounds.X - layout.Placements[0].CellBounds.Right, 0.000001);
        Assert.AreEqual(5, layout.Placements[2].CellBounds.Y - layout.Placements[0].CellBounds.Bottom, 0.000001);
        Assert.AreEqual(210, layout.Placements[^1].CellBounds.Right, 0.000001);
        Assert.AreEqual(297, layout.Placements[^1].CellBounds.Bottom, 0.000001);
    }

    [TestMethod]
    [DataRow(PdfLayOutScaleMode.OriginalSize)]
    [DataRow(PdfLayOutScaleMode.Fit)]
    [DataRow(PdfLayOutScaleMode.Fill)]
    [DataRow(PdfLayOutScaleMode.Custom)]
    public void ScaleAndOffset_KeepContainersFixedAndNeverExposeNeighboringContent(PdfLayOutScaleMode mode)
    {
        var settings = SingleCell with { ScaleMode = mode, ScalePercent = 400 };
        var original = PdfLayOutCalculator.Calculate(new(0, new(100, 80)), settings);
        var shifted = PdfLayOutCalculator.Calculate(new(0, new(100, 80)), settings with { Offset = new(-25, 33) });
        PdfLayOutPlacement cell = shifted.Placements.Single();
        Assert.AreEqual(original.GridBounds, shifted.GridBounds);
        Assert.AreEqual(original.Placements.Single().CellBounds, cell.CellBounds);
        Assert.AreEqual(original.Placements.Single().ImageBounds.X - 25, cell.ImageBounds.X, 0.000001);
        Assert.AreEqual(original.Placements.Single().ImageBounds.Y + 33, cell.ImageBounds.Y, 0.000001);
        AssertRect(cell.ContentBounds.Intersect(cell.CellBounds).Intersect(shifted.SheetBounds), cell.ClipBounds);
        Assert.IsTrue(cell.ClipBounds.X >= cell.CellBounds.X && cell.ClipBounds.Y >= cell.CellBounds.Y);
        Assert.IsTrue(cell.ClipBounds.Right <= cell.CellBounds.Right && cell.ClipBounds.Bottom <= cell.CellBounds.Bottom);
    }

    [TestMethod]
    public void CellBorders_RemainFixedWhenLabelsMoveCompletelyOutsideContainers()
    {
        var settings = new PdfLayOutSettings { Marks = new() { ShowCropMarks = false, ShowCellBorders = true } };
        var layout = PdfLayOutCalculator.Calculate(new(0, new(95, 52)), settings);
        var shifted = PdfLayOutCalculator.Calculate(new(0, new(95, 52)), settings with { Offset = new(500, 500) });
        PdfLayOutPrintMark borders = layout.Marks.Single();
        Assert.AreEqual(PdfLayOutMarkKind.CellBorder, borders.Kind);
        Assert.AreEqual(0.1, borders.StrokeWidth);
        Assert.AreEqual(40, borders.Lines.Count);
        Assert.IsTrue(shifted.Placements.All(cell => cell.ClipBounds.IsEmpty));
        CollectionAssert.AreEqual(borders.Lines.ToArray(), shifted.Marks.Single().Lines.ToArray());
        Assert.AreEqual(new PdfLayOutLine(new(0.05, 0.05), new(102.45, 0.05)), borders.Lines[0]);
        var hidden = PdfLayOutCalculator.Calculate(new(0, new(95, 52)), settings with
        {
            Marks = settings.Marks with { ShowCellBorders = false },
        });
        Assert.AreEqual(0, hidden.Marks.Count);
    }

    [TestMethod]
    public void CropMarks_UseContainerCornersLengthOffsetAndMillimeterStroke()
    {
        var layout = PdfLayOutCalculator.Calculate(new(0, new(100, 80)), MarkSettings());
        PdfLayOutPrintMark crop = layout.Marks.Single();

        Assert.AreEqual(PdfLayOutMarkKind.Crop, crop.Kind);
        Assert.AreEqual(0.1, crop.StrokeWidth);
        Assert.AreEqual(8, crop.Lines.Count);
        CollectionAssert.Contains(crop.Lines.ToArray(), new PdfLayOutLine(new(15, 20), new(18, 20)));
        CollectionAssert.Contains(crop.Lines.ToArray(), new PdfLayOutLine(new(20, 15), new(20, 18)));
    }

    [TestMethod]
    public void CropMarks_ZeroMarginsOnlyUseContainerGapsAndOmitPaperEdges()
    {
        var settings = new PdfLayOutSettings();
        var layout = PdfLayOutCalculator.Calculate(new(0, new(95, 52)), settings);
        PdfLayOutPrintMark crop = layout.Marks.Single();
        Assert.AreEqual(32, crop.Lines.Count);
        foreach (PdfLayOutLine line in crop.Lines)
        {
            bool horizontal = line.Start.Y == line.End.Y;
            var ink = horizontal
                ? new PdfLayOutRect(line.Start.X, line.Start.Y - crop.StrokeWidth / 2,
                    line.End.X - line.Start.X, crop.StrokeWidth)
                : new PdfLayOutRect(line.Start.X - crop.StrokeWidth / 2, line.Start.Y,
                    crop.StrokeWidth, line.End.Y - line.Start.Y);
            Assert.IsTrue(ink.X >= 0 && ink.Y >= 0 && ink.Right <= 210 && ink.Bottom <= 297);
            foreach (PdfLayOutPlacement cell in layout.Placements)
                Assert.IsTrue(ink.Intersect(cell.CellBounds).IsEmpty, $"标记 {line} 的描边进入容器。");
            if (horizontal)
            {
                Assert.IsTrue(line.Start.X >= 102.5 && line.End.X <= 107.5);
                Assert.IsTrue(line.Start.Y > 0 && line.Start.Y < 297);
            }
            else
            {
                Assert.IsTrue(line.Start.X > 0 && line.Start.X < 210);
            }
        }
        var outerOnly = PdfLayOutCalculator.Calculate(new(0, new(95, 52)), settings with
        {
            Marks = settings.Marks with { ShowInnerMarks = false },
        });
        Assert.AreEqual(0, outerOnly.Marks.Count);
    }

    [TestMethod]
    public void CropMarks_IgnoreLabelScaleCropAndOffsetEvenWhenLabelIsInvisible()
    {
        var settings = MarkSettings() with { Columns = 2, Rows = 2 };
        var original = PdfLayOutCalculator.Calculate(new(0, new(100, 80)), settings);
        var changed = PdfLayOutCalculator.Calculate(new(0, new(100, 80)), settings with
        {
            SourceCrop = new(20, 10, 5, 5), ScaleMode = PdfLayOutScaleMode.Custom,
            ScalePercent = 400, Offset = new(500, 500),
        });
        Assert.IsTrue(changed.Placements.All(cell => cell.ClipBounds.IsEmpty));
        CollectionAssert.AreEquivalent(original.Marks.Single().Lines.ToArray(), changed.Marks.Single().Lines.ToArray());
    }

    [TestMethod]
    [DataRow(1, 1, 5d)]
    [DataRow(2, 5, 0d)]
    [DataRow(2, 5, 2d)]
    public void CropMarks_OmitLinesWhenMarginsAndGapsHaveNoRoom(int columns, int rows, double gap)
    {
        var layout = PdfLayOutCalculator.Calculate(new(0, new(95, 52)), new()
        {
            GridMode = PdfLayOutGridMode.Manual, Columns = columns, Rows = rows,
            HorizontalGap = gap, VerticalGap = gap,
        });
        Assert.AreEqual(0, layout.Marks.Count);
    }

    [TestMethod]
    public void InnerMarks_CanBeHiddenAndShortenToFitNarrowGaps()
    {
        var settings = MarkSettings() with
        {
            Columns = 2, Rows = 2, HorizontalGap = 6, VerticalGap = 6,
            ScaleMode = PdfLayOutScaleMode.Fit,
            Marks = new() { Length = 10, Offset = 1 },
        };
        var all = PdfLayOutCalculator.Calculate(new(0, new(60, 60)), settings);
        var outer = PdfLayOutCalculator.Calculate(new(0, new(60, 60)), settings with { Marks = settings.Marks with { ShowInnerMarks = false } });

        Assert.AreEqual(32, all.Marks.Single().Lines.Count);
        Assert.AreEqual(16, outer.Marks.Single().Lines.Count);
        CollectionAssert.Contains(all.Marks.Single().Lines.ToArray(), new PdfLayOutLine(new(98, 20), new(100, 20)));
        foreach (PdfLayOutLine line in all.Marks.Single().Lines)
            foreach (PdfLayOutPlacement cell in all.Placements)
                Assert.IsFalse(CrossesInterior(line, cell.CellBounds), $"标记 {line} 侵入了容器 {cell.CellBounds}。");
    }

    [TestMethod]
    public void ZeroGaps_OmitUnavailableInnerMarksAndDeduplicateSharedEdges()
    {
        var settings = MarkSettings() with { Columns = 2, Rows = 2, HorizontalGap = 0, VerticalGap = 0, ScaleMode = PdfLayOutScaleMode.Fit };
        var layout = PdfLayOutCalculator.Calculate(new(0, new(60, 60)), settings);
        var outer = PdfLayOutCalculator.Calculate(new(0, new(60, 60)), settings with { Marks = settings.Marks with { ShowInnerMarks = false } });

        Assert.AreEqual(12, layout.Marks.Single().Lines.Count);
        CollectionAssert.AreEquivalent(outer.Marks.Single().Lines.ToArray(), layout.Marks.Single().Lines.ToArray());
    }

    [TestMethod]
    public void Marks_RespectPaperEdgesAndIndependentVisibilitySwitches()
    {
        var settings = MarkSettings() with
        {
            SheetMargins = new(1, 1, 1, 1), ScaleMode = PdfLayOutScaleMode.Fit,
            Marks = new() { Length = 20, Offset = 0 },
        };
        var layout = PdfLayOutCalculator.Calculate(new(0, new(100, 100)), settings);
        Assert.IsTrue(layout.Marks.All(mark => mark.Kind == PdfLayOutMarkKind.Crop));
        Assert.AreEqual(8, layout.Marks.Single().Lines.Count);
        Assert.IsTrue(layout.Marks.SelectMany(mark => mark.Lines).All(line =>
            line.Start.X >= 0.05 && line.Start.Y >= 0.05 && line.End.X <= 199.95 && line.End.Y <= 199.95));

        var hidden = PdfLayOutCalculator.Calculate(new(0, new(100, 80)), MarkSettings() with { Marks = new() { ShowCropMarks = false } });
        Assert.AreEqual(0, hidden.Marks.Count);
    }

    [TestMethod]
    public void OutputJob_ContainsResolvedReadOnlySheetsMatchingPreviewForMixedPageSizes()
    {
        var model = new PdfLayOutViewModel
        {
            IsAutomaticGrid = false, Columns = 2, Rows = 2, ScaleModeIndex = (int)PdfLayOutScaleMode.Fit,
            MarginLeft = 20, MarginTop = 20, MarginRight = 20, MarginBottom = 20,
            CropLeft = 5, CropBottom = 3, OffsetX = 2, OffsetY = -1, ShowCellBorders = true,
        };
        model.SetSourceDocument(new("mixed.pdf", [new(0, new(100, 80)), new(1, new(80, 120))]));
        var job = new PdfLayOutJob(model.SourceDocument!, model.CreateSettingsSnapshot());

        Assert.AreEqual(2, job.Sheets.Count);
        for (int index = 0; index < 2; index++)
        {
            model.SelectedPageIndex = index;
            CollectionAssert.AreEqual(model.CurrentLayout!.Placements.ToArray(), job.Sheets[index].Placements.ToArray());
            CollectionAssert.AreEqual(model.CurrentLayout.Marks.SelectMany(mark => mark.Lines).ToArray(), job.Sheets[index].Marks.SelectMany(mark => mark.Lines).ToArray());
        }
        PdfLayOutRect captured = job.Sheets[1].Placements[0].ImageBounds;
        model.OffsetX = 15;
        model.CropLeft = 10;
        Assert.AreEqual(captured, job.Sheets[1].Placements[0].ImageBounds);
        Assert.AreNotEqual(captured, model.CurrentLayout!.Placements[0].ImageBounds);
        Assert.Throws<NotSupportedException>(() => ((IList<PdfLayOutSheetLayout>)job.Sheets).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PdfLayOutPlacement>)job.Sheets[0].Placements).Clear());
    }

    private static PdfLayOutSettings MarkSettings() => new()
    {
        SheetSize = new(200, 200), SheetMargins = new(20, 20, 20, 20),
        GridMode = PdfLayOutGridMode.Manual, Columns = 1, Rows = 1,
        Marks = new() { Length = 3, Offset = 2 },
    };

    private static bool CrossesInterior(PdfLayOutLine line, PdfLayOutRect rect) =>
        line.Start.Y == line.End.Y
            ? line.Start.Y > rect.Y && line.Start.Y < rect.Bottom && line.Start.X < rect.Right && line.End.X > rect.X
            : line.Start.X > rect.X && line.Start.X < rect.Right && line.Start.Y < rect.Bottom && line.End.Y > rect.Y;

    private static void AssertRect(PdfLayOutRect expected, PdfLayOutRect actual)
    {
        Assert.AreEqual(expected.X, actual.X, 0.000001);
        Assert.AreEqual(expected.Y, actual.Y, 0.000001);
        Assert.AreEqual(expected.Width, actual.Width, 0.000001);
        Assert.AreEqual(expected.Height, actual.Height, 0.000001);
    }
}
