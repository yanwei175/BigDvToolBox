using System.Text.Json;

namespace BigDvToolBox_App.Features.ImageProcessing;

internal sealed record ImageProcessingDefaults(
    int LongEdge = ImageProcessingFileRules.DefaultLongEdge,
    string? OutputDirectory = null,
    ImageConflictPolicy ConflictPolicy = ImageConflictPolicy.AddSuffix);

internal interface IImageProcessingSettingsStore
{
    ImageProcessingDefaults Load();

    void SaveLongEdge(int value);

    void SaveOutputDirectory(string? value);

    void SaveConflictPolicy(ImageConflictPolicy value);
}

internal sealed class JsonImageProcessingSettingsStore : IImageProcessingSettingsStore
{
    private readonly string _settingsPath;

    internal JsonImageProcessingSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BigDvToolBox",
            "image-processing-settings.json"))
    {
    }

    internal JsonImageProcessingSettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public ImageProcessingDefaults Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new ImageProcessingDefaults();
            }

            string json = File.ReadAllText(_settingsPath);
            ImageProcessingDefaults? defaults =
                JsonSerializer.Deserialize<ImageProcessingDefaults>(json);
            if (defaults is null ||
                defaults.LongEdge <= 0 ||
                !Enum.IsDefined(defaults.ConflictPolicy))
            {
                return new ImageProcessingDefaults();
            }

            return defaults;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            return new ImageProcessingDefaults();
        }
    }

    public void SaveLongEdge(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        ImageProcessingDefaults current = Load();
        Save(current with { LongEdge = value });
    }

    public void SaveOutputDirectory(string? value)
    {
        ImageProcessingDefaults current = Load();
        Save(current with
        {
            OutputDirectory = string.IsNullOrWhiteSpace(value)
                ? null
                : Path.GetFullPath(value),
        });
    }

    public void SaveConflictPolicy(ImageConflictPolicy value)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        ImageProcessingDefaults current = Load();
        Save(current with { ConflictPolicy = value });
    }

    private void Save(ImageProcessingDefaults defaults)
    {
        string? directory = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("无法确定设置文件目录。");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            string json = JsonSerializer.Serialize(
                defaults,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
