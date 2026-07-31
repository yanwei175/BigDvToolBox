using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace BigDvToolBox_App.Features.PdfOutlineFonts;

internal interface IGhostscriptService
{
    string? VersionText { get; }

    bool IsSupported { get; }

    Task InitializeAsync(CancellationToken cancellationToken);

    Task ConvertAsync(
        string inputPath,
        string outputPath,
        PdfConvertMode convertMode,
        int dpi,
        CancellationToken cancellationToken);
}

internal sealed class GhostscriptService : IGhostscriptService
{
    internal const string GhostscriptVersion = "10.07.1";

    private const int GsArgEncodingUtf8 = 1;
    private const int GsQuit = -101;

    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;
    private IntPtr _libraryHandle;
    private GsApiNewInstance? _gsApiNewInstance;
    private GsApiDeleteInstance? _gsApiDeleteInstance;
    private GsApiSetArgEncoding? _gsApiSetArgEncoding;
    private GsApiInitWithArgs? _gsApiInitWithArgs;
    private GsApiExit? _gsApiExit;

    public string? VersionText { get; private set; }

    public bool IsSupported =>
        RuntimeInformation.ProcessArchitecture is Architecture.X86 or Architecture.X64;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Ghostscript 暂无 Windows ARM64 原生库。请使用 BigDvToolBox 的 x64 版本。");
        }

        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            string dllPath = await ExtractNativeLibraryAsync(cancellationToken);
            LoadNativeApi(dllPath);

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(dllPath);
            VersionText = string.IsNullOrWhiteSpace(version.FileVersion)
                ? GhostscriptVersion
                : version.FileVersion;
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task ConvertAsync(
        string inputPath,
        string outputPath,
        PdfConvertMode convertMode,
        int dpi,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string[] arguments = BuildArguments(
            inputPath,
            outputPath,
            convertMode,
            PdfOutlineFileRules.ClampDpi(dpi));
        await Task.Run(() => ExecuteGhostscript(arguments, cancellationToken), cancellationToken);
    }

    internal static string[] BuildArguments(
        string inputPath,
        string outputPath,
        PdfConvertMode convertMode,
        int dpi) =>
        convertMode switch
        {
            PdfConvertMode.Vector =>
            [
                "gs",
                "-dBATCH",
                "-dNOPAUSE",
                "-dQUIET",
                "-sDEVICE=pdfwrite",
                "-dNoOutputFonts",
                "-dCompatibilityLevel=1.4",
                $"-sOutputFile={outputPath}",
                inputPath,
            ],
            PdfConvertMode.Image =>
            [
                "gs",
                "-dBATCH",
                "-dNOPAUSE",
                "-dQUIET",
                "-sDEVICE=pdfimage24",
                "-dAutoRotatePages=/None",
                "-dTextAlphaBits=4",
                "-dGraphicsAlphaBits=4",
                "-dAlignToPixels=0",
                "-dGridFitTT=2",
                $"-r{PdfOutlineFileRules.ClampDpi(dpi)}",
                $"-sOutputFile={outputPath}",
                inputPath,
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(convertMode), convertMode, null),
        };

    private static string NativeDllName =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "gsdll32.dll",
            Architecture.X64 => "gsdll64.dll",
            _ => throw new PlatformNotSupportedException(
                $"不支持的进程架构：{RuntimeInformation.ProcessArchitecture}。"),
        };

    private static string ArchitectureFolder =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x64",
            _ => "unsupported",
        };

    private static async Task<string> ExtractNativeLibraryAsync(CancellationToken cancellationToken)
    {
        string targetDirectory = Path.Combine(
            Path.GetTempPath(),
            "BigDvToolBox",
            "PdfOutlineFonts",
            GhostscriptVersion,
            ArchitectureFolder);
        Directory.CreateDirectory(targetDirectory);

        string targetPath = Path.Combine(targetDirectory, NativeDllName);
        Assembly assembly = Assembly.GetExecutingAssembly();
        string resourceSuffix = $".Assets.{NativeDllName}";
        string? resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            throw new FileNotFoundException(
                $"未找到嵌入的 Ghostscript 资源 {NativeDllName}。请重新安装匹配当前架构的应用。");
        }

        byte[] embeddedHash;
        await using (Stream hashSource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"无法读取嵌入资源 {resourceName}。"))
        {
            embeddedHash = await SHA256.HashDataAsync(hashSource, cancellationToken);
        }

        if (File.Exists(targetPath))
        {
            await using var existingFile = new FileStream(
                targetPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            byte[] existingHash = await SHA256.HashDataAsync(existingFile, cancellationToken);
            if (CryptographicOperations.FixedTimeEquals(embeddedHash, existingHash))
            {
                return targetPath;
            }
        }

        string temporaryPath = $"{targetPath}.{Environment.ProcessId}.tmp";
        try
        {
            await using (Stream source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"无法读取嵌入资源 {resourceName}。"))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return targetPath;
    }

    private void LoadNativeApi(string nativeDllPath)
    {
        _libraryHandle = NativeLibrary.Load(nativeDllPath);
        _gsApiNewInstance = LoadDelegate<GsApiNewInstance>("gsapi_new_instance");
        _gsApiDeleteInstance = LoadDelegate<GsApiDeleteInstance>("gsapi_delete_instance");
        _gsApiSetArgEncoding = LoadDelegate<GsApiSetArgEncoding>("gsapi_set_arg_encoding");
        _gsApiInitWithArgs = LoadDelegate<GsApiInitWithArgs>("gsapi_init_with_args");
        _gsApiExit = LoadDelegate<GsApiExit>("gsapi_exit");
    }

    private T LoadDelegate<T>(string exportName)
        where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_libraryHandle, exportName, out IntPtr export))
        {
            throw new EntryPointNotFoundException($"未找到 Ghostscript 导出函数：{exportName}");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    private void ExecuteGhostscript(string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GsApiNewInstance newInstance = _gsApiNewInstance
            ?? throw new InvalidOperationException("Ghostscript API 尚未初始化。");
        GsApiDeleteInstance deleteInstance = _gsApiDeleteInstance
            ?? throw new InvalidOperationException("Ghostscript API 尚未初始化。");
        GsApiSetArgEncoding setArgEncoding = _gsApiSetArgEncoding
            ?? throw new InvalidOperationException("Ghostscript API 尚未初始化。");
        GsApiInitWithArgs initWithArgs = _gsApiInitWithArgs
            ?? throw new InvalidOperationException("Ghostscript API 尚未初始化。");
        GsApiExit exit = _gsApiExit
            ?? throw new InvalidOperationException("Ghostscript API 尚未初始化。");

        var argumentPointers = new IntPtr[arguments.Length];
        IntPtr argv = IntPtr.Zero;
        IntPtr instance = IntPtr.Zero;

        try
        {
            for (int index = 0; index < arguments.Length; index++)
            {
                argumentPointers[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
            }

            argv = Marshal.AllocHGlobal(IntPtr.Size * arguments.Length);
            for (int index = 0; index < arguments.Length; index++)
            {
                Marshal.WriteIntPtr(argv, index * IntPtr.Size, argumentPointers[index]);
            }

            ThrowIfFailed(newInstance(out instance, IntPtr.Zero), "gsapi_new_instance");
            ThrowIfFailed(setArgEncoding(instance, GsArgEncodingUtf8), "gsapi_set_arg_encoding");

            int initCode = initWithArgs(instance, arguments.Length, argv);
            if (initCode is not 0 and not GsQuit)
            {
                ThrowIfFailed(initCode, "gsapi_init_with_args");
            }

            int exitCode = exit(instance);
            if (exitCode is not 0 and not GsQuit)
            {
                ThrowIfFailed(exitCode, "gsapi_exit");
            }
        }
        finally
        {
            if (instance != IntPtr.Zero)
            {
                deleteInstance(instance);
            }

            if (argv != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(argv);
            }

            foreach (IntPtr pointer in argumentPointers)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
    }

    private static void ThrowIfFailed(int code, string apiName)
    {
        if (code < 0)
        {
            throw new InvalidOperationException(
                $"Ghostscript 调用失败：{apiName}，错误码 {code}。");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GsApiNewInstance(out IntPtr instance, IntPtr callerHandle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GsApiDeleteInstance(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GsApiSetArgEncoding(IntPtr instance, int encoding);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GsApiInitWithArgs(IntPtr instance, int argumentCount, IntPtr arguments);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GsApiExit(IntPtr instance);
}
