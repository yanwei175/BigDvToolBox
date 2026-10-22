using System.Text.Json;
using System.Text.Json.Serialization;

namespace BigDvToolBox_App.Features.PdfLayOut;

/// <summary>只保存毫米版式，文档、预览图、视图缩放和平移均不属于配置。</summary>
public sealed record PdfLayOutPreset(Guid Id, string Name, PdfLayOutSettings Settings)
{
    [JsonIgnore] public bool IsDefault => Id == Guid.Empty;
    [JsonIgnore] public string DisplayName => IsDefault ? $"{Name}（默认）" : Name;
    public static PdfLayOutPreset CreateDefault() => new(Guid.Empty, "默认配置", new());
}

public interface IPdfLayOutPresetStore
{
    Task<IReadOnlyList<PdfLayOutPreset>> LoadAsync(CancellationToken cancellationToken = default);
    Task<PdfLayOutPreset> AddAsync(string name, PdfLayOutSettings settings, CancellationToken cancellationToken = default);
    Task<PdfLayOutPreset> UpdateAsync(Guid id, string name, PdfLayOutSettings settings, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class JsonPdfLayOutPresetStore : IPdfLayOutPresetStore
{
    // 同一进程内不同页面实例的读改写不能互相覆盖。
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
    private readonly string _path;

    public JsonPdfLayOutPresetStore(string? path = null) => _path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BigDvToolBox", "pdf-layout-presets.json"));

    public async Task<IReadOnlyList<PdfLayOutPreset>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (EnsureDefault(library))
                await WriteAsync(library, cancellationToken).ConfigureAwait(false);
            return library.Presets.AsReadOnly();
        }
        finally { Gate.Release(); }
    }

    public async Task<PdfLayOutPreset> AddAsync(string name, PdfLayOutSettings settings, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await ReadAsync(cancellationToken).ConfigureAwait(false);
            EnsureDefault(library);
            var preset = new PdfLayOutPreset(Guid.NewGuid(), ValidateName(name), settings);
            ValidateSettings(settings);
            EnsureUniqueName(library, preset);
            library.Presets.Add(preset);
            await WriteAsync(library, cancellationToken).ConfigureAwait(false);
            return preset;
        }
        finally { Gate.Release(); }
    }

    public async Task<PdfLayOutPreset> UpdateAsync(Guid id, string name, PdfLayOutSettings settings, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await ReadAsync(cancellationToken).ConfigureAwait(false);
            EnsureDefault(library);
            int index = library.Presets.FindIndex(item => item.Id == id);
            if (index < 0) throw new InvalidOperationException("配置已不存在，请刷新配置列表。");
            var preset = new PdfLayOutPreset(id, ValidateName(name), settings);
            ValidateSettings(settings);
            EnsureUniqueName(library, preset);
            library.Presets[index] = preset;
            await WriteAsync(library, cancellationToken).ConfigureAwait(false);
            return preset;
        }
        finally { Gate.Release(); }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new InvalidOperationException("默认配置不允许删除。");
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var library = await ReadAsync(cancellationToken).ConfigureAwait(false);
            EnsureDefault(library);
            if (library.Presets.RemoveAll(item => item.Id == id) == 0)
                throw new InvalidOperationException("配置已不存在，请刷新配置列表。");
            await WriteAsync(library, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private async Task<Library> ReadAsync(CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Library library = await JsonSerializer.DeserializeAsync<Library>(stream, JsonOptions, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("配置文件为空。");
            if (library.Version != 1 || library.Presets is null)
                throw new InvalidDataException("配置文件的格式或版本不受支持。");
            var ids = new HashSet<Guid>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PdfLayOutPreset item in library.Presets)
            {
                if (item is null || !ids.Add(item.Id) || !names.Add(ValidateName(item.Name)))
                    throw new InvalidDataException("配置文件包含空项、重复标识或重复名称。");
                ValidateSettings(item.Settings);
            }
            return library;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            // 不用默认内容覆盖损坏的文件，避免丢失用户已有配置。
            throw new InvalidDataException($"配置文件无效，原文件已保留：{_path}。{exception.Message}", exception);
        }
    }

    private async Task WriteAsync(Library library, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, library, JsonOptions, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool EnsureDefault(Library library)
    {
        if (library.Presets.Any(item => item.IsDefault)) return false;
        PdfLayOutPreset preset = PdfLayOutPreset.CreateDefault();
        string name = preset.Name;
        for (int suffix = 2; library.Presets.Any(item => string.Equals(item.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)); suffix++)
            name = $"{preset.Name} {suffix}";
        library.Presets.Insert(0, preset with { Name = name });
        return true;
    }

    internal static string ValidateName(string? name)
    {
        string result = name?.Trim() ?? string.Empty;
        if (result.Length is < 1 or > 80) throw new ArgumentException("配置名称须为 1–80 个字符。");
        return result;
    }

    private static void EnsureUniqueName(Library library, PdfLayOutPreset preset)
    {
        if (library.Presets.Any(item => item.Id != preset.Id && string.Equals(item.Name.Trim(), preset.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("已有同名配置，请使用其他名称。");
    }

    internal static void ValidateSettings(PdfLayOutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        static bool NonNegative(double value) => double.IsFinite(value) && value >= 0;
        if (!settings.SheetSize.IsValid || !settings.SheetMargins.IsValid || !settings.SourceCrop.IsValid ||
            !NonNegative(settings.HorizontalGap) || !NonNegative(settings.VerticalGap) ||
            !double.IsFinite(settings.Offset.X) || !double.IsFinite(settings.Offset.Y) ||
            !double.IsFinite(settings.ScalePercent) || settings.ScalePercent is <= 0 or > 1000 ||
            !Enum.IsDefined(settings.GridMode) || !Enum.IsDefined(settings.ScaleMode) ||
            settings.Columns is < 1 or > PdfLayOutCalculator.MaximumGridCount ||
            settings.Rows is < 1 or > PdfLayOutCalculator.MaximumGridCount || settings.Marks is null ||
            !NonNegative(settings.Marks.Length) || !NonNegative(settings.Marks.Offset))
            throw new ArgumentException("配置包含无效的尺寸、边距、间距、缩放或标记参数。");
        double width = settings.SheetSize.Width - settings.SheetMargins.Left - settings.SheetMargins.Right;
        double height = settings.SheetSize.Height - settings.SheetMargins.Top - settings.SheetMargins.Bottom;
        if (width <= 0 || height <= 0 || settings.GridMode == PdfLayOutGridMode.Manual &&
            (width <= (settings.Columns - 1) * settings.HorizontalGap || height <= (settings.Rows - 1) * settings.VerticalGap))
            throw new ArgumentException("配置的边距或间距超出了印张可用区域。");
    }

    private sealed class Library
    {
        public Library() { }
        public int Version { get; init; } = 1;
        public List<PdfLayOutPreset> Presets { get; init; } = [];
    }
}
