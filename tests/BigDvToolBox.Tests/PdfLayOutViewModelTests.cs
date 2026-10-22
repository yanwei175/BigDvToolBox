using BigDvToolBox_App.Features.PdfLayOut;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutViewModelTests
{
    [TestMethod]
    public void RuntimeViewModel_StartsEmptyAndDisablesUnconnectedServices()
    {
        var model = new PdfLayOutViewModel();
        Assert.IsFalse(model.HasDocument);
        Assert.IsFalse(model.HasValidLayout);
        Assert.AreEqual(0, model.PreviewColumnCount);
        Assert.IsFalse(model.LoadDocumentCommand.CanExecute("sample.pdf"));
        Assert.IsFalse(model.ExportPdfCommand.CanExecute("output.pdf"));
        Assert.IsFalse(model.PrintCommand.CanExecute(null));
    }

    [TestMethod]
    public void DefaultPaper_IsA4Portrait()
    {
        var model = new PdfLayOutDesignViewModel();

        Assert.AreEqual("A4 · 210 × 297", model.PaperSizes[model.PaperSizeIndex]);
        Assert.AreEqual(210d, model.SheetWidth);
        Assert.AreEqual(297d, model.SheetHeight);
        Assert.AreEqual(1, model.PreviewColumnCount);
        Assert.AreEqual(1, model.PreviewRowCount);
        Assert.IsFalse(model.CanEditGrid);
    }

    [TestMethod]
    public void CellBorderSwitch_RebuildsPreviewAndIsCapturedForOutput()
    {
        var model = new PdfLayOutDesignViewModel { ShowCropMarks = false };
        Assert.IsFalse(model.ShowCellBorders);
        Assert.AreEqual(0, model.CurrentLayout!.Marks.Count);
        model.ShowCellBorders = true;
        Assert.IsTrue(model.CreateSettingsSnapshot().Marks.ShowCellBorders);
        Assert.IsTrue(model.CurrentLayout!.Marks.All(mark => mark.Kind == PdfLayOutMarkKind.CellBorder));
        Assert.AreEqual(4, model.CurrentLayout.Marks.Single().Lines.Count);
        model.ShowCellBorders = false;
        Assert.AreEqual(0, model.CurrentLayout!.Marks.Count);
    }

    [TestMethod]
    public void ManualGrid_UsesSelectedCountsRegardlessOfPaperAndGaps()
    {
        var model = new PdfLayOutDesignViewModel
        {
            IsAutomaticGrid = false,
            Columns = 3,
            Rows = 4,
            MarginLeft = 10,
            HorizontalGap = 8,
        };

        Assert.AreEqual(3, model.PreviewColumnCount);
        Assert.AreEqual(4, model.PreviewRowCount);
        Assert.IsTrue(model.CanEditGrid);
    }

    [TestMethod]
    public void AutomaticGrid_AccountsForMarginsAndGaps()
    {
        var model = new PdfLayOutDesignViewModel { PaperSizeIndex = 0 };
        Assert.AreEqual(2, model.PreviewColumnCount);
        Assert.AreEqual(2, model.PreviewRowCount);

        model.HorizontalGap = 25;
        Assert.AreEqual(1, model.PreviewColumnCount);

        model.MarginTop = 40;
        Assert.AreEqual(1, model.PreviewRowCount);

        model.MarginLeft = 200;
        Assert.AreEqual(0, model.PreviewColumnCount);
    }

    [TestMethod]
    public void PaperSelectionAndOrientation_KeepDimensionsInMillimeters()
    {
        var model = new PdfLayOutDesignViewModel { OrientationIndex = 1 };
        Assert.AreEqual(297d, model.SheetWidth);
        Assert.AreEqual(210d, model.SheetHeight);
        Assert.AreEqual(2, model.PaperSizeIndex);

        model.PaperSizeIndex = 1;
        Assert.AreEqual(420d, model.SheetWidth);
        Assert.AreEqual(297d, model.SheetHeight);

        model.SheetWidth = 400;
        Assert.AreEqual("自定义", model.PaperSizes[model.PaperSizeIndex]);
    }

    [TestMethod]
    public void LayoutSettings_NotifyDerivedPreviewCounts()
    {
        var model = new PdfLayOutDesignViewModel();
        var notifications = new List<string?>();
        model.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        model.MarginRight = 5;
        model.MarginBottom = 10;
        model.IsAutomaticGrid = false;

        CollectionAssert.Contains(notifications, nameof(model.PreviewColumnCount));
        CollectionAssert.Contains(notifications, nameof(model.PreviewRowCount));
        CollectionAssert.Contains(notifications, nameof(model.CanEditGrid));
    }

    [TestMethod]
    public void ManualGrid_NormalizesFractionalAndInvalidCounts()
    {
        var model = new PdfLayOutDesignViewModel { IsAutomaticGrid = false, Columns = 2.6, Rows = double.NaN };

        Assert.AreEqual(3d, model.Columns);
        Assert.AreEqual(1d, model.Rows);
        Assert.AreEqual(3, model.PreviewColumnCount);
        Assert.AreEqual(1, model.PreviewRowCount);
    }

    [TestMethod]
    public async Task LoadDocument_UsesActualPageMetadataAndSelection()
    {
        var services = new TestServices();
        var model = new PdfLayOutViewModel(services, services, services);
        await model.LoadDocumentCommand.ExecuteAsync("input.pdf");

        Assert.IsTrue(model.HasDocument);
        Assert.AreEqual(2, model.SourceDocument!.PageCount);
        Assert.AreEqual(2, model.PreviewColumnCount);
        model.SelectedPageIndex = 1;
        Assert.AreEqual(2, model.CurrentPageNumber);
        Assert.AreEqual(1, model.PreviewColumnCount);
        Assert.IsTrue(model.CanExport);
        Assert.IsTrue(model.CanPrint);
    }

    [TestMethod]
    public async Task FailedLoad_PreservesPreviousDocumentAndClearsBusyState()
    {
        var services = new TestServices();
        var model = new PdfLayOutViewModel(services, null, null);
        await model.LoadDocumentCommand.ExecuteAsync("input.pdf");
        PdfLayOutDocument original = model.SourceDocument!;
        services.FailLoading = true;
        await model.LoadDocumentCommand.ExecuteAsync("broken.pdf");

        Assert.AreSame(original, model.SourceDocument);
        Assert.IsFalse(model.IsBusy);
        Assert.IsNotNull(model.ErrorMessage);
    }

    [TestMethod]
    public async Task Export_CapturesSettingsAndPreventsConcurrentOperations()
    {
        var services = new TestServices { WaitForExport = true };
        var model = new PdfLayOutViewModel(services, services, services);
        await model.LoadDocumentCommand.ExecuteAsync("input.pdf");
        Task operation = model.ExportPdfCommand.ExecuteAsync("output.pdf");
        await services.ExportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(model.IsBusy);
        Assert.IsFalse(model.CanLoadDocument);
        Assert.IsFalse(model.CanPrint);
        model.MarginLeft = 15;
        Assert.AreEqual(0d, services.ExportedJob!.Settings.SheetMargins.Left);

        services.FinishExport.TrySetResult();
        await operation;
        Assert.IsFalse(model.IsBusy);
        Assert.AreEqual(Path.GetFullPath("output.pdf"), model.LastExportPath);
    }

    [TestMethod]
    public async Task Export_CancellationDoesNotReportSuccess()
    {
        var services = new TestServices { WaitForExport = true };
        var model = new PdfLayOutViewModel(services, services, null);
        await model.LoadDocumentCommand.ExecuteAsync("input.pdf");
        Task operation = model.ExportPdfCommand.ExecuteAsync("output.pdf");
        await services.ExportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.CancelOperationCommand.Execute(null);
        await operation;

        Assert.IsFalse(model.IsBusy);
        Assert.IsNull(model.LastExportPath);
        Assert.IsNull(model.ErrorMessage);
        StringAssert.Contains(model.StatusMessage, "取消");
    }

    [TestMethod]
    public async Task Export_RejectsSourcePathAndInvalidUnselectedPages()
    {
        var services = new TestServices();
        var model = new PdfLayOutViewModel(services, services, null);
        await model.LoadDocumentCommand.ExecuteAsync("input.pdf");
        await model.ExportPdfCommand.ExecuteAsync("input.pdf");
        Assert.IsNull(services.ExportedJob);
        StringAssert.Contains(model.ErrorMessage!, "源 PDF");

        model.SetSourceDocument(new("mixed.pdf", [new(0, new(100, 100)), new(1, new(500, 500))]));
        Assert.IsTrue(model.HasValidLayout);
        await model.ExportPdfCommand.ExecuteAsync("output.pdf");
        Assert.IsNull(services.ExportedJob);
        StringAssert.Contains(model.ErrorMessage!, "第 2 页");
    }

    [TestMethod]
    public async Task Print_ReportsSubmissionAndCancellationSeparately()
    {
        var services = new TestServices();
        var model = new PdfLayOutViewModel(services, null, services);
        await model.LoadDocumentCommand.ExecuteAsync("input.pdf");
        await model.PrintCommand.ExecuteAsync(null);
        Assert.AreEqual("已提交打印任务。", model.StatusMessage);

        services.PrintResult = PdfLayOutPrintResult.Cancelled;
        await model.PrintCommand.ExecuteAsync(null);
        Assert.AreEqual("已取消打印。", model.StatusMessage);
        Assert.IsNull(model.ErrorMessage);
    }

    private sealed class TestServices : IPdfLayOutDocumentService, IPdfLayOutExportService, IPdfLayOutPrintService
    {
        public bool FailLoading { get; set; }
        public bool WaitForExport { get; init; }
        public PdfLayOutJob? ExportedJob { get; private set; }
        public PdfLayOutPrintResult PrintResult { get; set; } = PdfLayOutPrintResult.Submitted;
        public TaskCompletionSource ExportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishExport { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PdfLayOutDocument> LoadAsync(string filePath, CancellationToken cancellationToken)
        {
            if (FailLoading) throw new InvalidDataException("无法读取 PDF。");
            return Task.FromResult(new PdfLayOutDocument(filePath, [new(0, new(100, 100)), new(1, new(148, 210))]));
        }

        public async Task ExportAsync(PdfLayOutJob job, string outputPath, CancellationToken cancellationToken)
        {
            ExportedJob = job;
            ExportStarted.TrySetResult();
            if (WaitForExport) await FinishExport.Task.WaitAsync(cancellationToken);
        }

        public Task<PdfLayOutPrintResult> PrintAsync(PdfLayOutJob job, CancellationToken cancellationToken) => Task.FromResult(PrintResult);
    }
}
