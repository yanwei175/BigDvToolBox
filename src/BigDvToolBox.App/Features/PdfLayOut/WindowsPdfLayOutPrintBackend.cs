using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Windows.Graphics.Printing.OptionDetails;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>系统打印预览和最终图片打印共用同一份内存 PDF，不读取页面截图。</summary>
internal sealed class WindowsPdfLayOutPrintBackend(Func<nint> getWindowHandle, Func<Panel> getPrintHost) : IPdfLayOutPrintBackend
{
    // 跨会话复用，防止上一次离线读取未结束时，重试再次进入同一个驱动。
    private readonly PdfLayOutPrintDriverReader _driverReader = new();
    public async Task<PdfLayOutPrintResult> PrintAsync(Stream imposedPdf, PdfLayOutJob job, CancellationToken cancellationToken)
    {
        var diagnostics = new PdfLayOutPrintDiagnostics(job);
        try { return await PrintAsync(imposedPdf, job, diagnostics, cancellationToken); }
        catch (Exception exception) { diagnostics.Error("PrintBackend", exception); throw; }
        finally
        {
            if (diagnostics.IsEnabled && diagnostics.HasSystemTask) _ = diagnostics.ObserveSystemErrorsAsync();
            else diagnostics.Dispose();
        }
    }

    public async Task<PdfLayOutPrintResult> PrintAsync(Stream imposedPdf, PdfLayOutJob job,
        PdfLayOutPrintDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nint window = getWindowHandle();
        if (window == 0) throw new InvalidOperationException("打印窗口尚未就绪。");
        if (!PrintManager.IsSupported()) throw new InvalidOperationException("当前系统不支持打印，请检查打印服务。");
        Panel host = getPrintHost();
        if (!host.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("请从页面线程发起打印。");
        diagnostics.Capture("Environment.Window", () => new
        {
            windowDpi = GetDpiForWindow(window), rasterizationScale = host.XamlRoot?.RasterizationScale,
            winUIAssembly = typeof(PrintDocument).Assembly.FullName,
        });
        WindowsPdfLayOutPrintRasterizer rasterizer;
        using (diagnostics.Stage("OpenImposedPdf"))
            rasterizer = await WindowsPdfLayOutPrintRasterizer.OpenAsync(imposedPdf, cancellationToken);
        using var ownedRasterizer = rasterizer;
        diagnostics.Record("OpenImposedPdf.Result", new { rasterizer.PageCount });
        if (rasterizer.PageCount != job.Sheets.Count) throw new InvalidDataException("打印文件的印张数量不一致。");
        await using var session = new PrintSession(job, rasterizer, host, diagnostics, _driverReader, cancellationToken);
        return await session.ShowAsync(window);
    }

    private sealed class PrintSession : IAsyncDisposable
    {
        private readonly PdfLayOutJob _job;
        private readonly WindowsPdfLayOutPrintRasterizer _rasterizer;
        private readonly Panel _host;
        private readonly PdfLayOutPrintDiagnostics _diagnostics;
        private readonly PdfLayOutPrintDriverReader _driverReader;
        private readonly PdfLayOutPrintSessionGeometry _geometry;
        private readonly PrintDocument _document = new();
        private readonly IPrintDocumentSource _source;
        private readonly CancellationTokenSource _lifetime;
        private readonly SemaphoreSlim _renderGate = new(1, 1);
        private readonly TaskCompletionSource<PdfLayOutPrintResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Task> _callbacks = [];
        private readonly Dictionary<int, Canvas> _previewPages = [];
        private PrintManager? _manager;
        private PrintTask? _printTask;
        private TypedEventHandler<PrintTask, PrintTaskCompletedEventArgs>? _taskCompletedHandler;
        private long _taskRevision;
        private PrintTaskOptions? _options;
        private readonly Dictionary<int, Task<PrintPageDescription>> _previewDescriptions = [];
        private CancellationTokenSource? _previewCancellation;
        private PrintCustomTextOptionDetails? _cropPreviewStatus;
        private PrintTaskOptionDetails? _optionDetails;
        private TypedEventHandler<PrintTaskOptionDetails, PrintTaskOptionChangedEventArgs>? _optionsChangedHandler;
        private bool _addingPages;
        private bool _disposed;

        public PrintSession(PdfLayOutJob job, WindowsPdfLayOutPrintRasterizer rasterizer, Panel host,
            PdfLayOutPrintDiagnostics diagnostics, PdfLayOutPrintDriverReader driverReader, CancellationToken token)
        {
            (_job, _rasterizer, _host) = (job, rasterizer, host);
            _diagnostics = diagnostics;
            _driverReader = driverReader;
            _geometry = new(job.Sheets.Select(sheet => sheet.SheetSize));
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
            bool shown;
            // 此阶段包含用户操作窗口的时间，不能当作纯渲染耗时。
            using (_diagnostics.Stage("ShowPrintUI", new { includesUserInteraction = true }))
                shown = await PrintManagerInterop.ShowPrintUIForWindowAsync(window);
            _diagnostics.Record("ShowPrintUI.Result", new { shown });
            if (!shown) return PdfLayOutPrintResult.Cancelled;
            // ShowPrintUI 只表示窗口已显示；必须等任务完成通知，才能释放 PDF 和事件。
            using (_diagnostics.Stage("WaitPrintTaskCompletion", new { includesUserInteractionAndDriverWait = true }))
                return await _completion.Task;
        }

        private void PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            _diagnostics.Record("PrintTaskRequested");
            // 系统可能从后台线程请求任务；用 deferral 回到窗口线程，统一事件和资源的生命周期。
            PrintTaskRequestedDeferral deferral = args.Request.GetDeferral();
            if (!_host.DispatcherQueue.TryEnqueue(() =>
            {
                try { if (!_disposed) CreatePrintTask(args.Request); }
                finally { deferral.Complete(); }
            }))
            {
                deferral.Complete();
                Fail("PrintTaskRequested.Dispatch", new InvalidOperationException("打印页面已经关闭。"));
            }
        }

        private void CreatePrintTask(PrintTaskRequest request)
        {
            using var stage = _diagnostics.Stage("CreatePrintTask");
            try
            {
                if (_printTask is not null && _taskCompletedHandler is not null) _printTask.Completed -= _taskCompletedHandler;
                UnregisterOptionsChanged();
                long taskRevision = Interlocked.Increment(ref _taskRevision);
                _printTask = request.CreatePrintTask(_diagnostics.TaskName,
                    sourceRequest =>
                    {
                        try
                        {
                            if (_disposed || taskRevision != Volatile.Read(ref _taskRevision)) return;
                            using var setSourceStage = _diagnostics.Stage("SetPrintSource");
                            sourceRequest.SetSource(_source);
                        }
                        catch (Exception exception) { Fail("SetPrintSource", exception); }
                    });
                _diagnostics.HasSystemTask = true;
                _taskCompletedHandler = (sender, args) => PrintTaskCompleted(taskRevision, args);
                _printTask.Completed += _taskCompletedHandler;
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
                ConfigureActualSizeOptions(_printTask.Options, taskRevision);
                RecordOptions("Requested", _printTask.Options);
            }
            catch (Exception exception) { Fail("CreatePrintTask", exception); }
        }

        private void PrintTaskCompleted(long taskRevision, PrintTaskCompletedEventArgs args)
        {
            if (_disposed || taskRevision != Volatile.Read(ref _taskRevision)) return;
            _diagnostics.Record("PrintTask.Completed", new
            {
                completion = args.Completion.ToString(),
                note = "该回调不提供驱动错误码。Failed 原样记录；Submitted 后的队列失败另查 WindowsPrintEvents。",
            });
            if (args.Completion == PrintTaskCompletion.Failed)
                Fail("PrintTask.Completed", new InvalidOperationException("系统未能提交打印任务，请检查打印机及纸张设置。"));
            else
                _completion.TrySetResult(args.Completion == PrintTaskCompletion.Submitted
                    ? PdfLayOutPrintResult.Submitted : PdfLayOutPrintResult.Cancelled);
        }

        private void Paginate(object sender, PaginateEventArgs args) => DispatchCallback(() =>
        {
            using var stage = _diagnostics.Stage("Paginate");
            if (_addingPages) return Task.CompletedTask;
            _options = args.PrintTaskOptions;
            ResetPreviewGeometry();
            _document.SetPreviewPageCount(_job.Sheets.Count, PreviewPageCountType.Final);
            return Task.CompletedTask;
        }, "Paginate");

        private void GetPreviewPage(object sender, GetPreviewPageEventArgs args) =>
            DispatchCallback(() => ShowPreviewPageAsync(args.PageNumber), "GetPreviewPage");

        private async Task ShowPreviewPageAsync(int pageNumber)
        {
            using var stage = _diagnostics.Stage("PreviewPage", new { page = pageNumber });
            if (_options is null || _addingPages || pageNumber < 1 || pageNumber > _job.Sheets.Count) return;
            long generation = _geometry.Revision;
            PrintTaskOptions options = _options;
            CancellationToken previewToken = _previewCancellation?.Token ?? _lifetime.Token;
            if (_previewPages.TryGetValue(pageNumber, out Canvas? cached))
            {
                _diagnostics.Record("PreviewPage.CacheHit", new { page = pageNumber });
                _document.SetPreviewPage(pageNumber, cached);
                return;
            }
            // PrintTaskOptions 是 agile；同步驱动读取放到后台，预览不再额外查询打印票据。
            if (!_previewDescriptions.TryGetValue(pageNumber, out var descriptionTask))
            {
                descriptionTask = ReadPageDescriptionAsync(options, pageNumber - 1, previewToken);
                _previewDescriptions.Add(pageNumber, descriptionTask);
            }
            PrintPageDescription description = await descriptionTask;
            if (_disposed || _addingPages || !_geometry.TryCreatePage(generation, pageNumber - 1,
                new(description.PageSize.Width, description.PageSize.Height),
                new(description.ImageableRect.X, description.ImageableRect.Y, description.ImageableRect.Width, description.ImageableRect.Height), out var plan)) return;
            PdfLayOutSize sheet = _job.Sheets[pageNumber - 1].SheetSize;
            PdfLayOutPrintRasterSize size = PdfLayOutPrintGeometry.CreateRasterSize(sheet, 120);
            Canvas page = await CreatePageAsync(pageNumber - 1, description, size, "Preview", plan, previewToken);
            if (_disposed || _addingPages || generation != _geometry.Revision) return;
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

        private void AddPages(object sender, AddPagesEventArgs args) => DispatchCallback(() => AddPagesAsync(args.PrintTaskOptions), "AddPages");

        private async Task AddPagesAsync(PrintTaskOptions options)
        {
            using var stage = _diagnostics.Stage("PrepareFinalPages");
            if (_addingPages) return;
            _addingPages = true;
            _geometry.Invalidate();
            long generation = _geometry.Revision;
            CancelPreview();
            _previewPages.Clear();
            _host.Children.Clear();
            IReadOnlyList<PdfLayOutPrintPageRange>? ranges = await _driverReader.ReadAsync(() =>
            {
                RecordOptions("Final", options);
                IReadOnlyList<PdfLayOutPrintPageRange>? result = options.CustomPageRanges.Count == 0 ? null
                    : options.CustomPageRanges.Select(range => new PdfLayOutPrintPageRange(
                        checked((uint)range.FirstPageNumber), checked((uint)range.LastPageNumber))).ToArray();
                return Task.FromResult(result);
            }, _lifetime.Token);
            var selectedPages = PdfLayOutPrintGeometry.SelectPages(_job.Sheets.Count, ranges);
            _diagnostics.Record("Final.PageSelection", new { pages = selectedPages.Select(index => index + 1).ToArray() });
            PdfLayOutPrintTicket? ticket = await ReadPrintTicketAsync(options, selectedPages[0], "Final");
            // WinUI 不公开写入 PageScaling 的接口；拒绝已知会缩放的票据，不能反向放大补偿。
            try { ticket?.EnsureActualSize(); }
            catch (InvalidOperationException exception)
            {
                // 先固定失败结果，再结束零页文档，防止 Windows 一直等待 AddPagesComplete。
                Fail("Print.ActualSizeValidation", exception);
                try
                {
                    using var completeStage = _diagnostics.Stage("AddPagesComplete.RejectedActualSize");
                    _document.AddPagesComplete();
                }
                catch (Exception completionError) { _diagnostics.Error("AddPagesComplete.RejectedActualSize", completionError); }
                return;
            }
            foreach (int index in selectedPages)
            {
                using var pageStage = _diagnostics.Stage("FinalPage", new { page = index + 1 });
                _lifetime.Token.ThrowIfCancellationRequested();
                PrintPageDescription description = await ReadPageDescriptionAsync(options, index, _lifetime.Token);
                _lifetime.Token.ThrowIfCancellationRequested();
                if (!_geometry.TryCreatePage(generation, index, new(description.PageSize.Width, description.PageSize.Height),
                    new(description.ImageableRect.X, description.ImageableRect.Y, description.ImageableRect.Width, description.ImageableRect.Height), out var plan)) return;
                double dpi = description.DpiX > 0 && description.DpiY > 0 ? Math.Min(description.DpiX, description.DpiY) : 300;
                PdfLayOutPrintRasterSize size = PdfLayOutPrintGeometry.CreateRasterSize(_job.Sheets[index].SheetSize, dpi);
                Canvas page = await CreatePageAsync(index, description, size, "Final", plan, _lifetime.Token);
                _lifetime.Token.ThrowIfCancellationRequested();
                using (_diagnostics.Stage("FinalPage.Layout", new { page = index + 1 })) AttachForLayout(page);
                // AddPage 同步提交该页的绘制内容；随后释放视图，避免累积所有页的高分辨率像素。
                using (_diagnostics.Stage("FinalPage.Submit", new { page = index + 1 })) _document.AddPage(page);
                _host.Children.Remove(page);
                page.Children.Clear();
            }
            using (_diagnostics.Stage("AddPagesComplete")) _document.AddPagesComplete();
        }

        private async Task<Canvas> CreatePageAsync(int index, PrintPageDescription description, PdfLayOutPrintRasterSize size, string phase,
            PdfLayOutPrintVisualPlan plan, CancellationToken token)
        {
            _diagnostics.RecordPage(phase, index, _job.Sheets[index].SheetSize, description.PageSize.Width, description.PageSize.Height,
                new(description.ImageableRect.X, description.ImageableRect.Y, description.ImageableRect.Width, description.ImageableRect.Height),
                description.DpiX, description.DpiY, size);
            PdfLayOutPrintBitmap pixels;
            using (_diagnostics.Stage("RenderGate.Wait", new { phase, page = index + 1 })) await _renderGate.WaitAsync(token);
            try
            {
                using var stage = _diagnostics.Stage("Rasterize", new { phase, page = index + 1 });
                pixels = await _rasterizer.RenderAsync(index, size, token, _diagnostics, phase);
            }
            finally { _renderGate.Release(); }
            token.ThrowIfCancellationRequested();
            WriteableBitmap bitmap;
            using (_diagnostics.Stage("CopyToWriteableBitmap", new { phase, page = index + 1, bytes = pixels.BgraPixels.LongLength }))
            {
                bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
                using (Stream buffer = bitmap.PixelBuffer.AsStream()) await buffer.WriteAsync(pixels.BgraPixels, token);
                bitmap.Invalidate();
            }
            token.ThrowIfCancellationRequested();
            bool preview = phase == "Preview";
            _diagnostics.Record("Page.VisualPlan", new
            {
                phase, page = index + 1, applicationScale = 1, paperDip = plan.Paper, sheetDip = plan.Sheet,
                printableAreaDip = plan.PrintableArea, contentClipDip = plan.ContentClip, plan.HasHardwareMargins,
                imageableSource = "PrintPageDescription",
                previewHardwareBoundaryConfirmed = preview && plan.HasHardwareMargins,
            });
            var page = new Canvas
            {
                Width = plan.Paper.Width, Height = plan.Paper.Height, UseLayoutRounding = false,
                FlowDirection = FlowDirection.LeftToRight,
                Background = preview ? new SolidColorBrush(Microsoft.UI.Colors.White) : null,
                HighContrastAdjustment = ElementHighContrastAdjustment.None,
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, plan.Paper.Width, plan.Paper.Height) },
            };
            var image = new Image
            {
                Source = bitmap, Stretch = Stretch.Fill, UseLayoutRounding = false,
                Width = plan.Sheet.Width, Height = plan.Sheet.Height,
            };
            PdfLayOutPoint origin = plan.SheetMillimetersToPaperDip(new(0, 0));
            Canvas.SetLeft(image, origin.X);
            Canvas.SetTop(image, origin.Y);
            // 容器为整纸坐标系，只裁切内容；绝不把整张图片缩放到 ImageableRect。
            var content = new Canvas
            {
                Width = plan.Paper.Width, Height = plan.Paper.Height, UseLayoutRounding = false,
                Clip = new RectangleGeometry { Rect = ToRect(plan.ContentClip) },
            };
            content.Children.Add(image);
            page.Children.Add(content);
            if (preview)
            {
                AddHardwareCropPreview(page, plan);
                await UpdateCropPreviewStatusAsync(plan, token);
            }
            return page;
        }

        private static Rect ToRect(PdfLayOutRect rectangle) => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

        private static void AddHardwareCropPreview(Canvas page, PdfLayOutPrintVisualPlan plan)
        {
            foreach (PdfLayOutRect area in plan.UnprintableAreas)
            {
                var mask = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = area.Width, Height = area.Height, IsHitTestVisible = false,
                    Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 220, 220, 220)),
                };
                Canvas.SetLeft(mask, area.X); Canvas.SetTop(mask, area.Y);
                page.Children.Add(mask);
            }
            if (!plan.HasHardwareMargins) return;
            var border = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = plan.PrintableArea.Width, Height = plan.PrintableArea.Height, IsHitTestVisible = false,
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.DimGray), StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
            Canvas.SetLeft(border, plan.PrintableArea.X); Canvas.SetTop(border, plan.PrintableArea.Y);
            page.Children.Add(border);
        }

        private void ConfigureActualSizeOptions(PrintTaskOptions options, long taskRevision)
        {
            try
            {
                var details = PrintTaskOptionDetails.GetFromPrintTaskOptions(options);
                _optionDetails = details;
                _optionsChangedHandler = (sender, args) =>
                {
                    // 应用自己的只读说明不改变打印几何，避免更新说明时反复重新分页。
                    if (args.OptionId is string optionId && optionId.StartsWith("BigDv", StringComparison.Ordinal)) return;
                    DispatchCallback(() =>
                    {
                        if (_addingPages || taskRevision != Volatile.Read(ref _taskRevision)) return Task.CompletedTask;
                        ResetPreviewGeometry();
                        _document.InvalidatePreview();
                        return Task.CompletedTask;
                    }, "Print.OptionsChanged");
                };
                details.OptionChanged += _optionsChangedHandler;
                var scale = details.CreateItemListOption("BigDvActualSize", "打印尺寸");
                scale.AddItem("ActualSize", "实际大小 100%（超出范围裁切）");
                scale.TrySetValue("ActualSize");
                scale.State = PrintOptionStates.None;
                details.DisplayedOptions.Add("BigDvActualSize");
                _cropPreviewStatus = details.CreateTextOption("BigDvCropPreview", "预览裁切范围（灰色区域不打印）");
                _cropPreviewStatus.TrySetValue("等待驱动提供可打印范围");
                _cropPreviewStatus.State = PrintOptionStates.None;
                details.DisplayedOptions.Add("BigDvCropPreview");
            }
            catch (Exception exception) { _diagnostics.Error("Print.CustomOptions", exception); }
        }

        private async Task UpdateCropPreviewStatusAsync(PdfLayOutPrintVisualPlan plan, CancellationToken token)
        {
            string value = plan.HasHardwareMargins
                ? $"可打印 {plan.PrintableArea.Width * 25.4 / 96:0.##} × {plan.PrintableArea.Height * 25.4 / 96:0.##} mm"
                : "未报告硬件边界；最终以驱动返回范围为准";
            try
            {
                var status = _cropPreviewStatus;
                if (status is null) return;
                await _driverReader.ReadAsync(() => Task.FromResult(
                    Equals(status.Value, value) || status.TrySetValue(value)), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { _diagnostics.Error("Print.UpdateCropPreviewStatus", exception); }
        }

        private Task<PrintPageDescription> ReadPageDescriptionAsync(PrintTaskOptions options, int index, CancellationToken token) =>
            _driverReader.ReadAsync(() => Task.FromResult(options.GetPageDescription((uint)index)), token);

        private Task<PdfLayOutPrintTicket?> ReadPrintTicketAsync(PrintTaskOptions options, int index, string phase) =>
            _driverReader.ReadAsync(() => ReadPrintTicketCoreAsync(options, index, phase), _lifetime.Token);

        private async Task<PdfLayOutPrintTicket?> ReadPrintTicketCoreAsync(PrintTaskOptions options, int index, string phase)
        {
            try
            {
                using var stage = _diagnostics.Stage("ReadPrintTicket", new { phase, page = index + 1 });
                PrintPageDescription description = options.GetPageDescription((uint)index);
                var info = new PrintPageInfo { MediaSize = options.MediaSize, Orientation = options.Orientation, PageSize = description.PageSize };
                using var stream = options.GetPagePrintTicket(info);
                if (stream.Size == 0 || stream.Size > PdfLayOutPrintTicket.MaximumBytes)
                    throw new InvalidDataException("打印票据为空或超过读取上限。");
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                // 原生调用自己持有流，取消会话只取消外层等待，不能从 UI 线程同步 Cancel 驱动调用。
                uint loaded = await reader.LoadAsync((uint)stream.Size).AsTask().ConfigureAwait(false);
                if (loaded != stream.Size) throw new InvalidDataException("打印票据读取不完整。");
                var bytes = new byte[checked((int)stream.Size)];
                reader.ReadBytes(bytes);
                using var input = new MemoryStream(bytes, writable: false);
                PdfLayOutPrintTicket ticket = PdfLayOutPrintTicket.Parse(input);
                _diagnostics.Record("Print.Ticket", new
                {
                    phase, page = index + 1, info.DpiX, info.DpiY,
                    pageSizeDip = new PdfLayOutSize(info.PageSize.Width, info.PageSize.Height),
                    mediaSize = info.MediaSize.ToString(), orientation = info.Orientation.ToString(),
                    ticket.ScalingStatus, ticket.BlockingReason, ticket.Features, ticket.Parameters, ticket.ImageableAreaDip, ticket.Xml,
                });
                return ticket;
            }
            catch (Exception exception)
            {
                _diagnostics.Error($"PrintTicket.{phase}", exception);
                _diagnostics.Record("Print.TicketUnavailable", new { phase, page = index + 1, applicationScale = 1, note = "应用保持原尺寸裁切；无法核实票据中的驱动缩放设置。" });
                return null;
            }
        }

        private void AttachForLayout(Canvas page)
        {
            _host.Children.Add(page);
            page.Measure(new Size(page.Width, page.Height));
            page.Arrange(new Rect(0, 0, page.Width, page.Height));
            page.UpdateLayout();
        }

        private void DispatchCallback(Func<Task> action, string stage)
        {
            if (_disposed) return;
            if (_host.DispatcherQueue.HasThreadAccess) _callbacks.Add(RunCallbackAsync(action, stage));
            else if (!_host.DispatcherQueue.TryEnqueue(() => { if (!_disposed) _callbacks.Add(RunCallbackAsync(action, stage)); }))
                Fail(stage + ".Dispatch", new InvalidOperationException("打印页面已经关闭。"));
        }

        private async Task RunCallbackAsync(Func<Task> action, string stage)
        {
            try { await action(); }
            catch (OperationCanceledException exception) when (_lifetime.IsCancellationRequested || exception.CancellationToken.IsCancellationRequested)
            { _diagnostics.Record(stage + ".Cancelled"); }
            catch (Exception exception) { Fail(stage, exception); }
        }

        private void Fail(string stage, Exception exception)
        {
            _diagnostics.Error(stage, exception);
            _completion.TrySetException(exception);
        }

        private void RecordOptions(string phase, PrintTaskOptions options) => _diagnostics.Capture("Print.Options", () => new
        {
            phase, mediaSize = options.MediaSize.ToString(), orientation = options.Orientation.ToString(),
            quality = options.PrintQuality.ToString(), colorMode = options.ColorMode.ToString(),
            duplex = options.Duplex.ToString(), copies = options.NumberOfCopies,
        });

        private void CancelPreview()
        {
            _previewCancellation?.Cancel();
            _previewCancellation?.Dispose();
            _previewCancellation = null;
        }

        private void ResetPreviewGeometry()
        {
            _geometry.Invalidate();
            CancelPreview();
            _previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _previewDescriptions.Clear();
            _previewPages.Clear();
            _host.Children.Clear();
        }

        private void UnregisterOptionsChanged()
        {
            if (_optionDetails is not null && _optionsChangedHandler is not null)
                _optionDetails.OptionChanged -= _optionsChangedHandler;
            _optionDetails = null;
            _optionsChangedHandler = null;
            _cropPreviewStatus = null;
        }

        public async ValueTask DisposeAsync()
        {
            using var stage = _diagnostics.Stage("PrintSession.Cleanup");
            _disposed = true;
            Interlocked.Increment(ref _taskRevision);
            _geometry.Invalidate();
            CancelPreview();
            _lifetime.Cancel();
            if (_manager is not null) _manager.PrintTaskRequested -= PrintTaskRequested;
            if (_printTask is not null && _taskCompletedHandler is not null) _printTask.Completed -= _taskCompletedHandler;
            UnregisterOptionsChanged();
            _document.Paginate -= Paginate;
            _document.GetPreviewPage -= GetPreviewPage;
            _document.AddPages -= AddPages;
            await Task.WhenAll(_callbacks);
            _previewPages.Clear();
            _previewDescriptions.Clear();
            _host.Children.Clear();
            _renderGate.Dispose();
            _lifetime.Dispose();
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);
}
