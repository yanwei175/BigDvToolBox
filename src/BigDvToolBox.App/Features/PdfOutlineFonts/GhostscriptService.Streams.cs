using System.Runtime.InteropServices;
using System.Text;

namespace BigDvToolBox_App.Features.PdfOutlineFonts;

internal sealed partial class GhostscriptService
{
    private GsApiSetStdio? _gsApiSetStdio;
    private GsApiSetPoll? _gsApiSetPoll;
    private GsApiRunString? _gsApiRunString;

    public async Task OutlineAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead || !output.CanWrite || ReferenceEquals(input, output))
            throw new ArgumentException("PDF 输入流必须可读，输出流必须可写，且不能为同一个流。");

        await ExecutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            await Task.Run(() =>
            {
                var io = new GhostscriptStreamIo(input, output, cancellationToken);
                ExecuteGhostscript(BuildMemoryOutlineArguments(), cancellationToken, io);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { ExecutionLock.Release(); }
    }

    internal static string[] BuildMemoryOutlineArguments() =>
    [
        "gs", "-dSAFER", "-dNOPAUSE", "-dQUIET",
        "-sDEVICE=pdfwrite", "-dNoOutputFonts", "-dCompatibilityLevel=1.7",
        "-dAutoRotatePages=/None", "-dPDFSTOPONERROR",
        "-sOutputFile=%ram%bigdvtoolbox-output.pdf",
        "--permit-file-read=%ram%bigdvtoolbox-input.pdf",
        "--permit-file-write=%ram%bigdvtoolbox-input.pdf",
        "--permit-file-read=%ram%bigdvtoolbox-output.pdf",
        "--permit-file-write=%ram%bigdvtoolbox-output.pdf",
        "--permit-file-write=%ram%bigdvtoolbox-next.pdf",
    ];

    private const string ConvertMemoryPdf =
        // PDF 解析需要随机访问。先拷到 Ghostscript 的 RAM 文件系统，避免直接读取
        // %stdin 时由解释器自动落地一个临时 PDF。runpdf 明确将内容按 PDF 解析。
        " /BigDvInput (%stdin) (r) file def" +
        " /BigDvRam (%ram%bigdvtoolbox-input.pdf) (w+) file def" +
        " /BigDvBuffer 65536 string def" +
        " { BigDvInput BigDvBuffer readstring /BigDvMore exch def" +
        " BigDvRam exch writestring BigDvMore not { exit } if } loop" +
        " BigDvRam flushfile BigDvRam 0 setfileposition BigDvRam runpdf" +
        // 切换输出文件会关闭上一份文档、补齐 xref/trailer，结果仍在 RAM 文件系统中。
        " << /OutputFile (%ram%bigdvtoolbox-next.pdf) >> setpagedevice";

    private const string ReadMemoryPdf =
        " /BigDvResult (%ram%bigdvtoolbox-output.pdf) (r) file def" +
        " /BigDvOutput (%stdout) (w) file def" +
        " { BigDvResult BigDvBuffer readstring /BigDvMore exch def" +
        " BigDvOutput exch writestring BigDvMore not { exit } if } loop" +
        " BigDvOutput flushfile BigDvResult closefile";

    private void RunMemoryConversion(IntPtr instance, GhostscriptStreamIo io, CancellationToken token)
    {
        Run(ConvertMemoryPdf);
        io.IsReadingResult = true;
        try { Run(ReadMemoryPdf); }
        finally { io.IsReadingResult = false; }
        if (io.BytesWritten == 0) throw new InvalidDataException("Ghostscript 没有生成转曲 PDF。");

        void Run(string script)
        {
            int code = _gsApiRunString!(instance, script, 0, out int exitCode);
            token.ThrowIfCancellationRequested();
            io.ThrowIfCallbackFailed();
            ThrowIfFailed(code, "gsapi_run_string", io.ErrorText);
            ThrowIfFailed(exitCode, "gsapi_run_string", io.ErrorText);
        }
    }

    // 回调不能让托管异常穿越原生调用栈。保存错误并返回失败码，回到托管侧后再抛出。
    private sealed class GhostscriptStreamIo(Stream input, Stream output, CancellationToken token)
    {
        private readonly StringBuilder _errors = new();
        private Exception? _callbackError;
        internal string ErrorText => _errors.ToString().Trim();
        internal bool IsReadingResult { get; set; }
        internal long BytesWritten { get; private set; }

        internal unsafe int Read(IntPtr caller, IntPtr buffer, int length)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                return input.Read(new Span<byte>((void*)buffer, length));
            }
            catch (Exception exception) { _callbackError ??= exception; return -1; }
        }

        internal unsafe int Write(IntPtr caller, IntPtr buffer, int length)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!IsReadingResult) return Error(caller, buffer, length);
                output.Write(new ReadOnlySpan<byte>((void*)buffer, length));
                BytesWritten += length;
                return length;
            }
            catch (Exception exception) { _callbackError ??= exception; return -1; }
        }

        internal unsafe int Error(IntPtr caller, IntPtr buffer, int length)
        {
            try
            {
                int count = Math.Min(length, Math.Max(0, 8192 - _errors.Length));
                if (count > 0) _errors.Append(Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)buffer, count)));
                return length;
            }
            catch (Exception exception) { _callbackError ??= exception; return -1; }
        }

        internal void ThrowIfCallbackFailed()
        {
            if (_callbackError is not null)
                throw new IOException("Ghostscript 内存流读写失败。", _callbackError);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GsIoCallback(IntPtr callerHandle, IntPtr buffer, int length);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GsPollCallback(IntPtr callerHandle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GsApiSetStdio(IntPtr instance, GsIoCallback input, GsIoCallback output, GsIoCallback error);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GsApiSetPoll(IntPtr instance, GsPollCallback poll);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GsApiRunString(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string script,
        int userErrors, out int exitCode);
}
