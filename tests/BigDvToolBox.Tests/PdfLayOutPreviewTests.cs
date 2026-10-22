using BigDvToolBox_App.Features.PdfLayOut;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutPreviewTests
{
    [TestMethod]
    public async Task Load_RendersFirstPageAndEnablesPageNavigation()
    {
        var service = new ControlledPreviewService();
        var model = new PdfLayOutViewModel(service, null, null);
        await model.LoadDocumentCommand.ExecuteAsync("source.pdf");

        Assert.AreEqual(0, model.PagePreview!.PageIndex);
        Assert.IsFalse(model.IsRenderingPreview);
        Assert.IsFalse(model.CanGoPreviousPage);
        Assert.IsTrue(model.CanGoNextPage);
        model.NextPageCommand.Execute(null);
        await model.PreviewTask;
        Assert.AreEqual(1, model.PagePreview!.PageIndex);
        Assert.IsTrue(model.CanGoPreviousPage);
    }

    [TestMethod]
    public async Task RapidPageSelection_IgnoresLateResponseFromPreviousPage()
    {
        var service = new ControlledPreviewService();
        var model = new PdfLayOutViewModel(service, null, null);
        await model.LoadDocumentCommand.ExecuteAsync("source.pdf");
        service.DeferRendering = true;

        model.SelectedPageIndex = 1;
        Task oldRequest = model.PreviewTask;
        Assert.IsNull(model.PagePreview);
        model.SelectedPageIndex = 2;
        Task newRequest = model.PreviewTask;

        Assert.IsTrue(service.Requests[0].Token.IsCancellationRequested);
        service.Requests[1].Completion.SetResult(Preview(2));
        await newRequest;
        Assert.AreEqual(2, model.PagePreview!.PageIndex);
        service.Requests[0].Completion.SetResult(Preview(1));
        await oldRequest;
        Assert.AreEqual(2, model.PagePreview!.PageIndex);
        Assert.IsFalse(model.IsRenderingPreview);
    }

    [TestMethod]
    public async Task FailedPreview_ClearsOldImageAndCanBeRetried()
    {
        var service = new ControlledPreviewService();
        var model = new PdfLayOutViewModel(service, null, null);
        await model.LoadDocumentCommand.ExecuteAsync("source.pdf");
        service.FailRendering = true;
        model.SelectedPageIndex = 1;
        await model.PreviewTask;
        Assert.IsNull(model.PagePreview);
        Assert.IsNotNull(model.PreviewErrorMessage);
        Assert.IsTrue(model.HasDocument);
        Assert.IsTrue(model.CanRefreshPreview);

        service.FailRendering = false;
        await model.RefreshPreviewCommand.ExecuteAsync(null);
        Assert.AreEqual(1, model.PagePreview!.PageIndex);
        Assert.IsNull(model.PreviewErrorMessage);
    }

    [TestMethod]
    public async Task StopPreview_PreventsPendingImageFromAppearingAfterPageUnloads()
    {
        var service = new ControlledPreviewService();
        var model = new PdfLayOutViewModel(service, null, null);
        await model.LoadDocumentCommand.ExecuteAsync("source.pdf");
        service.DeferRendering = true;
        model.SelectedPageIndex = 1;
        Task pending = model.PreviewTask;
        model.StopPreview();
        service.Requests[0].Completion.SetResult(Preview(1));
        await pending;

        Assert.IsNull(model.PagePreview);
        Assert.IsFalse(model.IsRenderingPreview);
        Assert.IsTrue(model.HasDocument);
    }

    [TestMethod]
    public async Task CancelLoading_CancelsRenderingAndRestoresInputState()
    {
        var service = new ControlledPreviewService { DeferRendering = true };
        var model = new PdfLayOutViewModel(service, null, null);
        Task load = model.LoadDocumentCommand.ExecuteAsync("source.pdf");
        Assert.IsTrue(model.IsBusy);
        model.CancelOperationCommand.Execute(null);
        Assert.IsTrue(service.Requests[0].Token.IsCancellationRequested);
        service.Requests[0].Completion.SetCanceled(service.Requests[0].Token);
        await load;

        Assert.IsFalse(model.IsBusy);
        Assert.IsFalse(model.IsRenderingPreview);
        Assert.IsNull(model.ErrorMessage);
        Assert.IsNull(model.PagePreview);
        Assert.IsTrue(model.CanLoadDocument);
    }

    [TestMethod]
    public void AutomaticA4Layout_ToleratesPdfUnitRounding()
    {
        var result = PdfLayOutCalculator.Calculate(new(0, new(210.00001, 297.00001)), new());
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(1, result.Columns);
        Assert.AreEqual(1, result.Rows);
    }

    private static PdfLayOutPagePreview Preview(int index) => new(index, [(byte)index], 1, 1);

    private sealed class ControlledPreviewService : IPdfLayOutDocumentService, IPdfLayOutPreviewService
    {
        public bool DeferRendering { get; set; }
        public bool FailRendering { get; set; }
        public List<(TaskCompletionSource<PdfLayOutPagePreview> Completion, CancellationToken Token)> Requests { get; } = [];

        public Task<PdfLayOutDocument> LoadAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult(new PdfLayOutDocument(filePath, Enumerable.Range(0, 3).Select(index => new PdfLayOutSourcePage(index, new(100, 100)))));

        public Task<PdfLayOutPagePreview> RenderAsync(PdfLayOutDocument document, int pageIndex, CancellationToken cancellationToken)
        {
            if (FailRendering) return Task.FromException<PdfLayOutPagePreview>(new IOException("测试渲染失败"));
            if (!DeferRendering) return Task.FromResult(Preview(pageIndex));
            var completion = new TaskCompletionSource<PdfLayOutPagePreview>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 故意不响应取消，验证 ViewModel 自身会拒绝过期的结果。
            Requests.Add((completion, cancellationToken));
            return completion.Task;
        }
    }
}
