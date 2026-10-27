namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>隔离不可取消的驱动 COM 调用。取消只结束等待，实际调用结束前仍占用同一读取通道。</summary>
internal sealed class PdfLayOutPrintDriverReader
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _timeout;

    public PdfLayOutPrintDriverReader(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(15);

    public async Task<T> ReadAsync<T>(Func<Task<T>> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!await _gate.WaitAsync(_timeout, token).ConfigureAwait(false))
            throw new TimeoutException("打印机仍在响应上一次连接，请检查连接后关闭打印窗口并重试。");

        // 不能在调用方取消时释放 gate，否则连续重试会堆积未结束的驱动调用。
        Task<T> worker = Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                return await read().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        });
        _ = worker.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await worker.WaitAsync(_timeout, token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            throw new TimeoutException("读取打印机设置超时，请检查打印机连接，关闭打印窗口后重试。");
        }
    }
}
