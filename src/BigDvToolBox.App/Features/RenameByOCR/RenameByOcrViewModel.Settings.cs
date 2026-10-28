using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BigDvToolBox_App.Features.RenameByOCR;

internal partial class RenameByOcrViewModel
{
    private readonly IOcrSettingsStore _settingsStore;
    private readonly OcrModelDirectoryService _modelDirectoryService;
    private OcrSettings _settings = new();
    private bool _settingsInitialized;
    private bool _updatingSettings;
    private string _modelDirectoryError = string.Empty;

    public ObservableCollection<OcrLanguageOption> Languages { get; } = [];
    public bool IsOcrReady => _settingsInitialized && string.IsNullOrEmpty(OcrConfigurationError);
    public string LanguageSummary => _settings.SelectedLanguages.Length == 0
        ? "未选择语言" : string.Join(" + ", _settings.SelectedLanguages);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigure))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanNavigate))]
    public partial bool IsSettingsBusy { get; private set; }

    [ObservableProperty]
    public partial string ModelDirectory { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOcrReady))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    public partial string OcrConfigurationError { get; private set; } = "正在读取 OCR 设置…";

    [ObservableProperty]
    public partial string SettingsSaveError { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int LayoutIndex { get; set; }

    [ObservableProperty]
    public partial bool DisableDictionaries { get; set; }

    public async Task InitializeOcrSettingsAsync()
    {
        if (_settingsInitialized || IsSettingsBusy) return;
        IsSettingsBusy = true;
        try
        {
            _settings = await Task.Run(_settingsStore.Load);
            _updatingSettings = true;
            LayoutIndex = (int)_settings.Layout;
            DisableDictionaries = _settings.DisableDictionaries;
            _updatingSettings = false;
            ModelDirectory = _settings.ModelDirectory ?? _modelDirectoryService.DefaultDirectory;
            if (_settings.ModelDirectory is null)
                await Task.Run(_modelDirectoryService.EnsureDefaultModels);
            _settingsInitialized = true;
            await RefreshLanguagesCoreAsync();
        }
        catch (Exception e)
        {
            // Keep the chooser usable so an inaccessible default directory is recoverable.
            _settingsInitialized = true;
            _modelDirectoryError = $"OCR 设置初始化失败：{e.Message}。可选择其他模型目录。";
            OcrConfigurationError = _modelDirectoryError;
        }
        finally
        {
            _updatingSettings = false;
            IsSettingsBusy = false;
            OnPropertyChanged(nameof(LanguageSummary));
        }
    }

    public async Task RefreshLanguagesAsync()
    {
        if (!CanConfigure) return;
        IsSettingsBusy = true;
        try { await RefreshLanguagesCoreAsync(); }
        finally { IsSettingsBusy = false; }
    }

    public async Task SetModelDirectoryAsync(string? directory)
    {
        if (!CanConfigure) return;
        IsSettingsBusy = true;
        try
        {
            _settings = _settings with { ModelDirectory = directory is null ? null : Path.GetFullPath(directory) };
            ModelDirectory = _settings.ModelDirectory ?? _modelDirectoryService.DefaultDirectory;
            SaveSettings();
            if (_settings.ModelDirectory is null)
                await Task.Run(_modelDirectoryService.EnsureDefaultModels);
            await RefreshLanguagesCoreAsync();
        }
        catch (Exception e)
        {
            _modelDirectoryError = $"模型目录读取失败：{e.Message}";
            OcrConfigurationError = _modelDirectoryError;
        }
        finally { IsSettingsBusy = false; }
    }

    private async Task RefreshLanguagesCoreAsync()
    {
        IReadOnlyList<OcrModelInfo> models = [];
        string directoryError = string.Empty;
        try { models = await Task.Run(() => _modelDirectoryService.Scan(ModelDirectory)); }
        catch (Exception e) { directoryError = $"模型目录不可访问：{e.Message}"; }

        foreach (OcrLanguageOption option in Languages)
            option.PropertyChanged -= Language_PropertyChanged;
        Languages.Clear();
        var available = models.ToDictionary(model => model.Code, model => model.Available, StringComparer.Ordinal);
        foreach (string code in _settings.SelectedLanguages.Concat(available.Keys).Distinct(StringComparer.Ordinal))
        {
            var option = new OcrLanguageOption
            {
                Code = code,
                IsAvailable = available.GetValueOrDefault(code),
                IsSelected = _settings.SelectedLanguages.Contains(code, StringComparer.Ordinal),
            };
            option.PropertyChanged += Language_PropertyChanged;
            Languages.Add(option);
        }

        _modelDirectoryError = directoryError;
        ValidateLanguageSelection();
    }

    private void Language_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OcrLanguageOption.IsSelected) || _updatingSettings) return;
        // Keep the previous language order; append newly selected languages.
        string[] selected = _settings.SelectedLanguages
            .Where(code => Languages.Any(option => option.Code == code && option.IsSelected))
            .Concat(Languages.Where(option => option.IsSelected).Select(option => option.Code))
            .Distinct(StringComparer.Ordinal).ToArray();
        _settings = _settings with { SelectedLanguages = selected };
        ValidateLanguageSelection();
        SaveSettings();
        OnPropertyChanged(nameof(LanguageSummary));
    }

    private void ValidateLanguageSelection()
    {
        string[] missing = Languages.Where(option => option.IsSelected && !option.IsAvailable)
            .Select(option => option.Code).ToArray();
        OcrConfigurationError = !string.IsNullOrEmpty(_modelDirectoryError) ? _modelDirectoryError
            : _settings.SelectedLanguages.Length == 0 ? "请至少选择一种 OCR 语言。"
            : missing.Length > 0 ? $"所选模型缺失或为空：{string.Join(", ", missing)}。请补齐模型或取消勾选。"
            : string.Empty;
    }

    partial void OnLayoutIndexChanged(int value)
    {
        if (_updatingSettings || !_settingsInitialized) return;
        if (!Enum.IsDefined(typeof(OcrLayout), value)) return;
        _settings = _settings with { Layout = (OcrLayout)value };
        SaveSettings();
    }

    partial void OnDisableDictionariesChanged(bool value)
    {
        if (_updatingSettings || !_settingsInitialized) return;
        _settings = _settings with { DisableDictionaries = value };
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
            SettingsSaveError = string.Empty;
        }
        catch (Exception e)
        {
            SettingsSaveError = $"设置保存失败，本次会话仍可使用：{e.Message}";
        }
    }

    private OcrEngineOptions CreateOptionsSnapshot()
    {
        // Re-scan selected paths just before creating the engine (files may be removed externally).
        try
        {
            var present = _modelDirectoryService.Scan(ModelDirectory)
                .Where(model => model.Available).Select(model => model.Code).ToHashSet(StringComparer.Ordinal);
            if (_settings.SelectedLanguages.Length == 0 || _settings.SelectedLanguages.Any(code => !present.Contains(code)))
                throw new InvalidOperationException("所选语言模型缺失或为空，请刷新语言列表并检查选择。");
        }
        catch (Exception e)
        {
            OcrConfigurationError = $"无法使用当前模型：{e.Message}";
            throw;
        }
        return new(ModelDirectory, string.Join("+", _settings.SelectedLanguages), _settings.DisableDictionaries);
    }

    internal static OcrSegmentationMode ResolveLayout(OcrLayout layout, RenameMode mode) => layout switch
    {
        OcrLayout.Auto => OcrSegmentationMode.Auto,
        OcrLayout.SingleLine => OcrSegmentationMode.SingleLine,
        OcrLayout.SingleWord => OcrSegmentationMode.SingleWord,
        _ => mode == RenameMode.FixedRegion ? OcrSegmentationMode.SingleWord : OcrSegmentationMode.Auto,
    };
}
