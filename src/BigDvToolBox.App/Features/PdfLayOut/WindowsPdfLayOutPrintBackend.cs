using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Printing;
using WinRT.Interop;

namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>系统打印预览和最终图片打印共用同一份内存 PDF，不读取页面截图。</summary>
internal sealed class WindowsPdfLayOutPrintBackend(Func<nint> getWindowHandle, Func<Panel> getPrintHost) : IPdfLayOutPrintBackend
{
    public async Task<PdfLayOutPrintResult> PrintAsync(Stream imposedPdf, PdfLayOutJob job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nint window = getWindowHandle();
        if (window == 0) throw new InvalidOperationException("打印窗口尚未就绪。");
        if (!PrintManager.IsSupported()) throw new InvalidOperationException("当前系统不支持打印，请检查打印服务。");
        Panel host = getPrintHost();
        if (!host.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("请从页面线程发起打印。");
        using WindowsPdfLayOutPrintRasterizer rasterizer = await WindowsPdfLayOutPrintRasterizer.OpenAsync(imposedPdf, cancellationToken);
        if (rasterizer.PageCount != job.Sheets.Count) throw new InvalidDataException("打印文件的印张数量不一致。");
        await using var session = new PrintSession(job, rasterizer, host, cancellationToken);
        return await session.ShowAsync(window);
    }

    private sealed class PrintSession : IAsyncDisposable
    {
        private readonly PdfLayOutJob _job;
        private readonly WindowsPdfLayOutPrintRasterizer _rasterizer;
        private readonly Panel _host;
        private readonly PrintDocument _document = new();
        private readonly IPrintDocumentSource _source;
        private readonly CancellationTokenSource _lifetime;
        private readonly SemaphoreSlim _renderGate = new(1, 1);
        private readonly TaskCompletionSource<PdfLayOutPrintResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Task> _callbacks = [];
        private readonly Dictionary<int, Canvas> _previewPages = [];
        private PrintManager? _manager;
        private PrintTask? _printTask;
        private PrintTaskOptions? _options;
        private long _generation;
        private bool _addingPages;
        private bool _disposed;

        public PrintSession(PdfLayOutJob job, WindowsPdfLayOutPrintRasterizer rasterizer, Panel host, CancellationToken token)
        {
            (_job, _rasterizer, _host) = (job, rasterizer, host);
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            _source = _document.DocumentSource;
            _document.Paginate += Paginate;
            _document.GetPreviewPage += GetPreviewPage;
            _document.AddPages += AddPages;
        }

        public async Task<PdfLayOutPrintResult> ShowAsync(nint window)
        {
            _manager = PrintManagerInterop.GetForWindow(window);
            _manager.PrintTaskRequested += PrintTaskRequested;
            using CancellationTokenRegistration registration = _lifetime.Token.Register(() => _completion.TrySetCanceled(_lifetime.Token));
            if (!await PrintManagerInterop.ShowPrintUIForWindowAsync(window)) return PdfLayOutPrintResult.Cancelled;
            // ShowPrintUI 只表示窗口已显示；必须等任务完成通知，才能释放 PDF 和事件。
            return await _completion.Task;
        }

        private void PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            // 系统可能从后台线程请求任务；用 deferral 回到窗口线程，统一事件和资源的生命周期。
            PrintTaskRequestedDeferral deferral = args.Request.GetDeferral();
            if (!_host.DispatcherQueue.TryEnqueue(() =>
            {
                try { if (!_disposed) CreatePrintTask(args.Request); }
                finally { deferral.Complete(); }
            }))
            {
                deferral.Complete();
                _completion.TrySetException(new InvalidOperationException("打印页面已经关闭。"));
            }
        }

        private void CreatePrintTask(PrintTaskRequest request)
        {
            try
            {
                if (_printTask is not null) _printTask.Completed -= PrintTaskCompleted;
                _printTask = request.CreatePrintTask(_job.DisplayName,
                    sourceRequest =>
                    {
                        try { sourceRequest.SetSource(_source); }
                        catch (Exception exception) { _completion.TrySetException(exception); }
                    });
                _printTask.Completed += PrintTaskCompleted;
                PdfLayOutSize sheet = _job.Settings.SheetSize;
                _printTask.Options.Orientation = sheet.Width > sheet.Height ? PrintOrientation.Landscape : PrintOrientation.Portrait;
                double shortEdge = Math.Min(sheet.Width, sheet.Height), longEdge = Math.Max(sheet.Width, sheet.Height);
                if (Math.Abs(shortEdge - 210) < 0.01 && Math.Abs(longEdge - 297) < 0.01)
                    _printTask.Options.MediaSize = PrintMediaSize.IsoA4;
                else if (Math.Abs(shortEdge - 297) < 0.01 && Math.Abs(longEdge - 420) < 0.01)
                    _printTask.Options.MediaSize = PrintMediaSize.IsoA3;
                else if (Math.Abs(shortEdge - 320) < 0.01 && Math.Abs(longEdge - 450) < 0.01)
                    _printTask.Options.MediaSize = PrintMediaSize.IsoSRA3;
                _printTask.Options.PageRangeOptions.AllowAllPages = true;
                _printTask.Options.PageRangeOptions.AllowCurrentPage = false;
                _printTask.Options.PageRangeOptions.AllowCustomSetOfPages = true;
                _printTask.Options.DisplayedOptions.Add(StandardPrintTaskOptions.CustomPageRanges);
            }
            catch (Exception exception) { _completion.TrySetException(exception); }
        }

        private void PrintTaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs args)
        {
            if (args.Completion == PrintTaskCompletion.Failed)
                _completion.TrySetException(new InvalidOperationException("系统未能提交打印任务，请检查打印机及纸张设置。"));
            else
                _completion.TrySetResult(args.Completion == PrintTaskCompletion.Submitted
                    ? PdfLayOutPrintResult.Submitted : PdfLayOutPrintResult.Cancelled);
        }

        private void Paginate(object sender, PaginateEventArgs args) => DispatchCallback(() =>
        {
            if (_addingPages) return Task.CompletedTask;
            _generation++;
            _options = args.PrintTaskOptions;
            _previewPages.Clear();
            _host.Children.Clear();
            _document.SetPreviewPageCount(_job.Sheets.Count, PreviewPageCountType.Final);
            return Task.CompletedTask;
        });

        private void GetPreviewPage(object sender, GetPreviewPageEventArgs args) =>
            DispatchCallback(() => ShowPreviewPageAsync(args.PageNumber));

        private async Task ShowPreviewPageAsync(int pageNumber)
        {
            if (_options is null || _addingPages || pageNumber < 1 || pageNumber > _job.Sheets.Count) return;
            long generation = _generation;
            if (_previewPages.TryGetValue(pageNumber, out Canvas? cached))
            {
                _document.SetPreviewPage(pageNumber, cached);
                return;
            }
            PrintPageDescription description = _options.GetPageDescription((uint)(pageNumber - 1));
            PdfLayOutSize sheet = _job.Sheets[pageNumber - 1].SheetSize;
            PdfLayOutPrintRasterSize size = PdfLayOutPrintGeometry.CreateRasterSize(sheet, 120);
            Canvas page = await CreatePageAsync(pageNumber - 1, description, size);
            if (_disposed || _addingPages || generation != _generation) return;
            if (_previewPages.TryGetValue(pageNumber, out cached))
            {
                _document.SetPreviewPage(pageNumber, cached);
                return;
            }
            // 只缓存最近请求的三张预览，不提前解码全部印张。
            if (_previewPages.Count >= 3)
            {
                int oldest = _previewPages.Keys.First();
                _host.Children.Remove(_previewPages[oldest]);
                _previewPages.Remove(oldest);
            }
            _previewPages.Add(pageNumber, page);
            AttachForLayout(page);
            _document.SetPreviewPage(pageNumber, page);
        }

        private void AddPages(object sender, AddPagesEventArgs args) => DispatchCallback(() => AddPagesAsync(args.PrintTaskOptions));

        private async Task AddPagesAsync(PrintTaskOptions options)
        {
            if (_addingPages) return;
            _addingPages = true;
            _generation++;
            _previewPages.Clear();
            _host.Children.Clear();
            IReadOnlyList<PdfLayOutPrintPageRange>? ranges = options.CustomPageRanges.Count == 0 ? null
                : options.CustomPageRanges.Select(range => new PdfLayOutPrintPageRange(
                    checked((uint)range.FirstPageNumber), checked((uint)range.LastPageNumber))).ToArray();
            foreach (int index in PdfLayOutPrintGeometry.SelectPages(_job.Sheets.Count, ranges))
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                PrintPageDescription description = options.GetPageDescription((uint)index);
                double dpi = description.DpiX > 0 && description.DpiY > 0 ? Math.Min(description.DpiX, description.DpiY) : 300;
                PdfLayOutPrintRasterSize size = PdfLayOutPrintGeometry.CreateRasterSize(_job.Sheets[index].SheetSize, dpi);
                Canvas page = await CreatePageAsync(index, description, size);
                _lifetime.Token.ThrowIfCancellationRequested();
                AttachForLayout(page);
                // AddPage 同步提交该页的绘制内容；随后释放视图，避免累积所有页的高分辨率像素。
                _document.AddPage(page);
                _host.Children.Remove(page);
                page.Children.Clear();
            }
            _document.AddPagesComplete();
        }

        private async Task<Canvas> CreatePageAsync(int index, PrintPageDescription description, PdfLayOutPrintRasterSize size)
        {
            CancellationToken token = _lifetime.Token;
            PdfLayOutPrintBitmap pixels;
            await _renderGate.WaitAsync(token);
            try { pixels = await _rasterizer.RenderAsync(index, size, token); }
            finally { _renderGate.Release(); }
            token.ThrowIfCancellationRequested();
            var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
            using (Stream buffer = bitmap.PixelBuffer.AsStream())
                await buffer.WriteAsync(pixels.BgraPixels, token);
            bitmap.Invalidate();
            token.ThrowIfCancellationRequested();
            PdfLayOutPrintVisualPlan plan = PdfLayOutPrintGeometry.CreateVisualPlan(_job.Sheets[index].SheetSize,
                new(description.PageSize.Width, description.PageSize.Height));
            var page = new Canvas
            {
                Width = plan.Paper.Width, Height = plan.Paper.Height, UseLayoutRounding = false,
                FlowDirection = FlowDirection.LeftToRight,
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                HighContrastAdjustment = ElementHighContrastAdjustment.None,
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, plan.Paper.Width, plan.Paper.Height) },
            };
            var image = new Image
            {
                Source = bitmap, Stretch = Stretch.Fill, UseLayoutRounding = false,
                Width = plan.Sheet.Width, Height = plan.Sheet.Height,
            };
            Canvas.SetLeft(image, plan.Sheet.X);
            Canvas.SetTop(image, plan.Sheet.Y);
            page.Children.Add(image);
            return page;
        }

        private void AttachForLayout(Canvas page)
        {
            _host.Children.Add(page);
            page.Measure(new Size(page.Width, page.Height));
            page.Arrange(new Rect(0, 0, page.Width, page.Height));
            page.UpdateLayout();
        }

        private void DispatchCallback(Func<Task> action)
        {
            if (_disposed) return;
            if (_host.DispatcherQueue.HasThreadAccess) _callbacks.Add(RunCallbackAsync(action));
            else if (!_host.DispatcherQueue.TryEnqueue(() => { if (!_disposed) _callbacks.Add(RunCallbackAsync(action)); }))
                _completion.TrySetException(new InvalidOperationException("打印页面已经关闭。"));
        }

        private async Task RunCallbackAsync(Func<Task> action)
        {
            try { await action(); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) { _completion.TrySetException(exception); }
        }

        public async ValueTask DisposeAsync()
        {
            _disposed = true;
            _lifetime.Cancel();
            if (_manager is not null) _manager.PrintTaskRequested -= PrintTaskRequested;
            if (_printTask is not null) _printTask.Completed -= PrintTaskCompleted;
            _document.Paginate -= Paginate;
            _document.GetPreviewPage -= GetPreviewPage;
            _document.AddPages -= AddPages;
            await Task.WhenAll(_callbacks);
            _previewPages.Clear();
            _host.Children.Clear();
            _renderGate.Dispose();
            _lifetime.Dispose();
        }
    }
}
