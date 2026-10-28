using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BigDvToolBox_App.Features.RenameByOCR;

internal enum OcrLayout
{
    FollowTask,
    Auto,
    SingleLine,
    SingleWord,
}

internal sealed record OcrSettings
{
    public string? ModelDirectory { get; init; }
    public string[] SelectedLanguages { get; init; } = ["eng", "ces", "pol", "rus"];
    public OcrLayout Layout { get; init; } = OcrLayout.FollowTask;
    public bool DisableDictionaries { get; init; }
}

// Strings keep the task snapshot immutable, including language order.
internal sealed record OcrEngineOptions(string ModelDirectory, string Languages, bool DisableDictionaries);

internal interface IOcrSettingsStore
{
    OcrSettings Load();
    void Save(OcrSettings settings);
}

internal sealed class JsonOcrSettingsStore : IOcrSettingsStore
{
    private readonly string _path;

    internal JsonOcrSettingsStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BigDvToolBox", "ocr-settings.json");

    public OcrSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            OcrSettings? settings = JsonSerializer.Deserialize<OcrSettings>(File.ReadAllText(_path));
            if (settings is null || !Enum.IsDefined(settings.Layout) ||
                settings.SelectedLanguages is null ||
                settings.SelectedLanguages.Any(code => !OcrModelDirectoryService.IsLanguageCode(code)))
                return new();

            return settings with
            {
                SelectedLanguages = settings.SelectedLanguages.Distinct(StringComparer.Ordinal).ToArray(),
                ModelDirectory = string.IsNullOrWhiteSpace(settings.ModelDirectory) ? null : settings.ModelDirectory,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(OcrSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

internal sealed record OcrModelInfo(string Code, bool Available);

internal sealed class OcrModelDirectoryService
{
    private readonly string _bundledDirectory;
    public string DefaultDirectory { get; }

    internal OcrModelDirectoryService(string? defaultDirectory = null, string? bundledDirectory = null)
    {
        DefaultDirectory = defaultDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BigDvToolBox", "OCR", "tessdata");
        _bundledDirectory = bundledDirectory ?? Path.Combine(
            AppContext.BaseDirectory, "Features", "RenameByOCR", "tessdata");
    }

    public void EnsureDefaultModels()
    {
        Directory.CreateDirectory(DefaultDirectory);
        string marker = Path.Combine(DefaultDirectory, ".initialized");
        if (File.Exists(marker)) return;

        foreach (string code in new OcrSettings().SelectedLanguages)
        {
            string target = Path.Combine(DefaultDirectory, code + ".traineddata");
            if (File.Exists(target)) continue;
            string temporaryPath = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(Path.Combine(_bundledDirectory, code + ".traineddata"), temporaryPath);
                try { File.Move(temporaryPath, target, overwrite: false); }
                catch (IOException) when (File.Exists(target)) { /* Another page initialized it. */ }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        File.WriteAllText(marker, "Bundled eng/ces/pol/rus seeded once; user files are never overwritten.");
    }

    public IReadOnlyList<OcrModelInfo> Scan(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".traineddata", StringComparison.OrdinalIgnoreCase))
            .Where(path => IsLanguageCode(Path.GetFileNameWithoutExtension(path)))
            .Select(path => new OcrModelInfo(Path.GetFileNameWithoutExtension(path), new FileInfo(path).Length > 0))
            .OrderBy(model => model.Code, StringComparer.Ordinal)
            .ToArray();

    internal static bool IsLanguageCode(string? code) => !string.IsNullOrWhiteSpace(code) &&
        code is not ("osd" or "equ") && char.IsAsciiLetter(code[0]) &&
        code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}

public sealed partial class OcrLanguageOption : ObservableObject
{
    public required string Code { get; init; }
    public bool IsAvailable { get; init; }
    public string DisplayName => (Code switch
    {
        "eng" => "英语", "ces" => "捷克语", "pol" => "波兰语", "rus" => "俄语",
        "chi_sim" => "简体中文", "chi_tra" => "繁体中文", "jpn" => "日语",
        "kor" => "韩语", "deu" => "德语", "fra" => "法语", "spa" => "西班牙语",
        "ita" => "意大利语", "por" => "葡萄牙语", "ara" => "阿拉伯语",
        _ => Code,
    }) + $" ({Code})" + (IsAvailable ? "" : " — 缺失或为空");

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
