using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace BigDvToolBox_App.Features.PdfLayOut;

public partial class PdfLayOutViewModel
{
    private IPdfLayOutPresetStore? _presetStore;
    private bool _presetsInitialized;
    private bool _isApplyingPreset;
    private PdfLayOutPreset? _selectedPreset;
    private PdfLayOutSettings _presetBaseline = new();

    public ObservableCollection<PdfLayOutPreset> Presets { get; } = [PdfLayOutPreset.CreateDefault()];
    public PdfLayOutPreset? SelectedPreset
    {
        get => _selectedPreset;
        set => TrySelectPreset(value);
    }
    [ObservableProperty] public partial bool IsPresetBusy { get; private set; }

    public bool HasUnsavedPresetChanges => CreateSettingsSnapshot() != _presetBaseline;
    public string PresetHint => HasUnsavedPresetChanges ? "当前配置有未保存的修改，请先保存再切换。"
        : "切换配置自动加载。默认配置不可删除。";

    public bool TrySelectPreset(PdfLayOutPreset? preset)
    {
        if (ReferenceEquals(preset, SelectedPreset)) return true;
        if (preset is null || !CanManagePresets || !Presets.Contains(preset)) return false;
        if (!EnsurePresetSaved()) return false;
        try
        {
            ApplyPresetSettings(preset.Settings);
            SetActivePreset(preset);
            _presetBaseline = preset.Settings;
            NotifyPresetDirtyState();
            ErrorMessage = null;
            StatusMessage = $"已加载配置“{preset.Name}”。";
            return true;
        }
        catch (Exception exception) { ReportInputError($"加载配置失败：{exception.Message}"); return false; }
    }

    private bool EnsurePresetSaved()
    {
        if (!HasUnsavedPresetChanges) return true;
        ReportInputError("当前配置有未保存的修改，请先保存当前配置，或新增配置保存当前版式，再切换。");
        return false;
    }

    private void SetActivePreset(PdfLayOutPreset? preset)
    {
        if (SetProperty(ref _selectedPreset, preset, nameof(SelectedPreset))) NotifyPresetState();
    }

    private void NotifyPresetDirtyState()
    {
        OnPropertyChanged(nameof(HasUnsavedPresetChanges));
        OnPropertyChanged(nameof(PresetHint));
    }

    public bool CanManagePresets => _presetStore is not null && !IsBusy;
    public bool CanLoadPreset => CanManagePresets && SelectedPreset is not null;
    public bool CanDeletePreset => CanLoadPreset && !SelectedPreset!.IsDefault;

    public async Task InitializePresetsAsync()
    {
        if (_presetsInitialized || !CanManagePresets) return;
        await RunPresetOperationAsync(async () =>
        {
            IReadOnlyList<PdfLayOutPreset> items = await _presetStore!.LoadAsync();
            ReplacePresets(items, Guid.Empty);
            ApplyPresetSettings(SelectedPreset!.Settings);
            _presetBaseline = SelectedPreset.Settings;
            NotifyPresetDirtyState();
            _presetsInitialized = true;
            StatusMessage = "已加载默认配置。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanManagePresets))]
    private Task RefreshPresetsAsync()
    {
        if (!EnsurePresetSaved()) return Task.CompletedTask;
        return RunPresetOperationAsync(async () =>
        {
            Guid id = SelectedPreset?.Id ?? Guid.Empty;
            ReplacePresets(await _presetStore!.LoadAsync(), id);
            ApplyPresetSettings(SelectedPreset!.Settings);
            _presetBaseline = SelectedPreset.Settings;
            NotifyPresetDirtyState();
            _presetsInitialized = true;
            StatusMessage = "配置列表已刷新，并加载当前配置。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanManagePresets))]
    private Task AddPresetAsync(string? name) => RunPresetOperationAsync(async () =>
    {
        PdfLayOutPreset preset = await _presetStore!.AddAsync(name ?? string.Empty, CreateSettingsSnapshot());
        Presets.Add(preset);
        SetActivePreset(preset);
        _presetBaseline = preset.Settings;
        NotifyPresetDirtyState();
        StatusMessage = $"已将当前版式保存为“{preset.Name}”。";
    });

    [RelayCommand(CanExecute = nameof(CanLoadPreset))]
    private Task SavePresetAsync()
    {
        PdfLayOutPreset? selected = SelectedPreset;
        return RunPresetOperationAsync(async () =>
        {
            if (selected is null) return;
            PdfLayOutSettings snapshot = CreateSettingsSnapshot();
            PdfLayOutPreset updated = await _presetStore!.UpdateAsync(selected.Id, selected.Name, snapshot);
            ReplacePreset(updated);
            _presetBaseline = snapshot;
            NotifyPresetDirtyState();
            StatusMessage = $"已保存配置“{updated.Name}”。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanLoadPreset))]
    private Task RenamePresetAsync(string? name)
    {
        PdfLayOutPreset? selected = SelectedPreset;
        return RunPresetOperationAsync(async () =>
        {
            if (selected is null) return;
            // 重命名只修改名称，不把尚未保存的当前参数写入该配置。
            PdfLayOutPreset updated = await _presetStore!.UpdateAsync(selected.Id, name ?? string.Empty, selected.Settings);
            ReplacePreset(updated);
            StatusMessage = $"配置已重命名为“{updated.Name}”。";
        });
    }

    [RelayCommand(CanExecute = nameof(CanDeletePreset))]
    private Task DeletePresetAsync()
    {
        PdfLayOutPreset? selected = SelectedPreset;
        if (selected?.IsDefault != false) return Task.CompletedTask;
        if (!EnsurePresetSaved()) return Task.CompletedTask;
        return RunPresetOperationAsync(async () =>
        {
            await _presetStore!.DeleteAsync(selected.Id);
            Presets.Remove(selected);
            SetActivePreset(Presets.First(item => item.IsDefault));
            ApplyPresetSettings(SelectedPreset!.Settings);
            _presetBaseline = SelectedPreset.Settings;
            NotifyPresetDirtyState();
            StatusMessage = $"已删除“{selected.Name}”，并加载默认配置。";
        });
    }

    private async Task RunPresetOperationAsync(Func<Task> action)
    {
        if (!CanManagePresets) return;
        IsPresetBusy = true;
        ErrorMessage = null;
        try { await action(); }
        catch (Exception exception) { ReportInputError($"配置操作失败：{exception.Message}"); }
        finally { IsPresetBusy = false; }
    }

    private void ReplacePresets(IReadOnlyList<PdfLayOutPreset> items, Guid selectedId)
    {
        SetActivePreset(null);
        Presets.Clear();
        foreach (PdfLayOutPreset item in items) Presets.Add(item);
        SetActivePreset(Presets.FirstOrDefault(item => item.Id == selectedId) ?? Presets.First(item => item.IsDefault));
    }

    private void ReplacePreset(PdfLayOutPreset updated)
    {
        int index = Presets.ToList().FindIndex(item => item.Id == updated.Id);
        if (index >= 0) Presets[index] = updated;
        else Presets.Add(updated);
        SetActivePreset(updated);
    }

    private void ApplyPresetSettings(PdfLayOutSettings settings)
    {
        JsonPdfLayOutPresetStore.ValidateSettings(settings);
        _isApplyingPreset = true;
        _isUpdatingPaperSize = true;
        try
        {
            SheetWidth = settings.SheetSize.Width;
            SheetHeight = settings.SheetSize.Height;
            OrientationIndex = SheetWidth > SheetHeight ? 1 : 0;
            double shortEdge = Math.Min(SheetWidth, SheetHeight), longEdge = Math.Max(SheetWidth, SheetHeight);
            PaperSizeIndex = (shortEdge, longEdge) switch
            {
                (320, 450) => 0, (297, 420) => 1, (210, 297) => 2, (225, 320) => 3, _ => CustomPaperSizeIndex,
            };
            MarginLeft = settings.SheetMargins.Left; MarginTop = settings.SheetMargins.Top;
            MarginRight = settings.SheetMargins.Right; MarginBottom = settings.SheetMargins.Bottom;
            CropLeft = settings.SourceCrop.Left; CropTop = settings.SourceCrop.Top;
            CropRight = settings.SourceCrop.Right; CropBottom = settings.SourceCrop.Bottom;
            OffsetX = settings.Offset.X; OffsetY = settings.Offset.Y;
            IsAutomaticGrid = settings.GridMode == PdfLayOutGridMode.Automatic;
            Columns = settings.Columns; Rows = settings.Rows;
            HorizontalGap = settings.HorizontalGap; VerticalGap = settings.VerticalGap;
            ScaleModeIndex = (int)settings.ScaleMode; ScalePercent = settings.ScalePercent;
            ShowCropMarks = settings.Marks.ShowCropMarks; ShowInnerMarks = settings.Marks.ShowInnerMarks;
            CropMarkLength = settings.Marks.Length; CropMarkOffset = settings.Marks.Offset;
            ShowCellBorders = settings.Marks.ShowCellBorders;
        }
        finally { _isUpdatingPaperSize = false; _isApplyingPreset = false; }
        RebuildLayout();
    }

    private void NotifyPresetState()
    {
        OnPropertyChanged(nameof(CanManagePresets));
        OnPropertyChanged(nameof(CanLoadPreset));
        OnPropertyChanged(nameof(CanDeletePreset));
        RefreshPresetsCommand.NotifyCanExecuteChanged();
        AddPresetCommand.NotifyCanExecuteChanged();
        SavePresetCommand.NotifyCanExecuteChanged();
        RenamePresetCommand.NotifyCanExecuteChanged();
        DeletePresetCommand.NotifyCanExecuteChanged();
    }
}
