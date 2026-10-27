using BigDvToolBox_App.Features.PdfLayOut;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutPrintDriverReaderTests
{
    [TestMethod]
    public async Task OfflineSynchronousRead_DoesNotRunOnCallingThread_AndCancellationReleasesCaller()
    {
        var reader = new PdfLayOutPrintDriverReader(TimeSpan.FromSeconds(5));
        int caller = Environment.CurrentManagedThreadId;
        bool returned = false, enteredInline = false;
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        Task<int> read = reader.ReadAsync(() =>
        {
            enteredInline = Environment.CurrentManagedThreadId == caller && !returned;
            started.SetResult(Environment.CurrentManagedThreadId);
            release.Wait(); // 模拟不响应 CancellationToken 的同步驱动 COM 调用。
            return Task.FromResult(1);
        }, cancellation.Token);
        returned = true;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsFalse(enteredInline, "同步驱动调用不能阻塞发起读取的事件处理程序。");
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.IsFalse(release.IsSet, "取消应在驱动恢复之前结束调用方的等待。");
        }
        finally { release.Set(); }
        Assert.AreEqual(2, await reader.ReadAsync(() => Task.FromResult(2), CancellationToken.None));
    }

    [TestMethod]
    public async Task RetryAfterCancel_WaitsForOldNativeRead_AndNeverReturnsItsStaleResult()
    {
        var reader = new PdfLayOutPrintDriverReader(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        int active = 0, maximumActive = 0;
        Task<int> first = reader.ReadAsync<int>(() =>
        {
            maximumActive = Interlocked.Increment(ref active);
            started.SetResult();
            release.Wait();
            Interlocked.Decrement(ref active);
            throw new IOException("旧驱动读取稍后才失败");
        }, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
            bool retried = false;
            Task<int> retry = reader.ReadAsync(() =>
            {
                maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
                retried = true;
                Interlocked.Decrement(ref active);
                return Task.FromResult(42);
            }, CancellationToken.None);
            Assert.IsFalse(retry.IsCompleted);
            Assert.IsFalse(retried);
            release.Set();
            Assert.AreEqual(42, await retry.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.AreEqual(1, maximumActive, "新旧会话不能同时进入驱动。");
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task Timeout_AndRepeatedRetry_DoNotAccumulateNativeCalls()
    {
        var reader = new PdfLayOutPrintDriverReader(TimeSpan.FromMilliseconds(100));
        using var release = new ManualResetEventSlim();
        int calls = 0;
        Task<int> first = reader.ReadAsync(() =>
        {
            Interlocked.Increment(ref calls);
            release.Wait();
            return Task.FromResult(1);
        }, CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(TimeSpan.FromSeconds(3)));
            for (int i = 0; i < 3; i++)
                await Assert.ThrowsAsync<TimeoutException>(() => reader.ReadAsync(() =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.FromResult(2);
                }, CancellationToken.None));
            Assert.AreEqual(1, calls);
        }
        finally { release.Set(); }
        Assert.AreEqual(3, await reader.ReadAsync(() => Task.FromResult(3), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    public async Task CancelWhileQueued_DoesNotStartTheCanceledRead_AndLaterRetrySucceeds()
    {
        var reader = new PdfLayOutPrintDriverReader(TimeSpan.FromSeconds(5));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Task<int> first = reader.ReadAsync(() =>
        {
            started.SetResult(); release.Wait(); return Task.FromResult(1);
        }, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var cancellation = new CancellationTokenSource();
            bool entered = false;
            Task<int> queued = reader.ReadAsync(() => { entered = true; return Task.FromResult(2); }, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(2)));
            release.Set();
            await first;
            Assert.AreEqual(3, await reader.ReadAsync(() => Task.FromResult(3), CancellationToken.None));
            Assert.IsFalse(entered);
        }
        finally { release.Set(); }
    }
}
