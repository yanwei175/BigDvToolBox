using BigDvToolBox_App.Features.PdfLayOut;
using System.Text.Json;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class PdfLayOutPresetTests
{
    [TestMethod]
    public async Task Store_CreatesCurrentDefaultsAndRoundTripsCompleteLayoutWithoutViewState()
    {
        using var fixture = new Fixture();
        var store = new JsonPdfLayOutPresetStore(fixture.Path);
        PdfLayOutPreset initial = (await store.LoadAsync()).Single();
        Assert.IsTrue(initial.IsDefault);
        Assert.AreEqual(new PdfLayOutViewModel().CreateSettingsSnapshot(), initial.Settings);

        PdfLayOutSettings settings = SampleSettings();
        PdfLayOutPreset added = await store.AddAsync("  标签 A  ", settings);
        var reopened = new JsonPdfLayOutPresetStore(fixture.Path);
        PdfLayOutPreset restored = (await reopened.LoadAsync()).Single(item => item.Id == added.Id);
        Assert.AreEqual("标签 A", restored.Name);
        Assert.AreEqual(settings, restored.Settings);
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Path));
        Assert.AreEqual(1, json.RootElement.GetProperty("version").GetInt32());
        JsonElement saved = json.RootElement.GetProperty("presets")[1].GetProperty("settings");
        CollectionAssert.AreEquivalent(new[]
        {
            "sheetSize", "sheetMargins", "sourceCrop", "offset", "gridMode", "columns", "rows",
            "horizontalGap", "verticalGap", "scaleMode", "scalePercent", "marks",
        }, saved.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.AreEqual(2, saved.GetProperty("sheetSize").EnumerateObject().Count());
    }

    [TestMethod]
    public async Task Store_AllowsDefaultEditsButRejectsDeletionAndDuplicateOrInvalidChanges()
    {
        using var fixture = new Fixture();
        var store = new JsonPdfLayOutPresetStore(fixture.Path);
        await store.LoadAsync();
        PdfLayOutPreset changed = await store.UpdateAsync(Guid.Empty, "常用默认", SampleSettings());
        Assert.IsTrue(changed.IsDefault);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.DeleteAsync(Guid.Empty));
        PdfLayOutPreset added = await store.AddAsync("标签", new());
        string before = await File.ReadAllTextAsync(fixture.Path);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.AddAsync(" 标签 ", new()));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.UpdateAsync(added.Id, changed.Name, new()));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.UpdateAsync(added.Id, added.Name, new() { HorizontalGap = double.NaN }));
        Assert.AreEqual(before, await File.ReadAllTextAsync(fixture.Path));
        await store.UpdateAsync(added.Id, "新名称", SampleSettings());
        Assert.AreEqual("新名称", (await store.LoadAsync()).Single(item => item.Id == added.Id).Name);
        await store.DeleteAsync(added.Id);
        Assert.AreEqual(changed, (await store.LoadAsync()).Single());
    }

    [TestMethod]
    public async Task Store_PreservesDamagedJsonAndSerializesConcurrentReadModifyWrite()
    {
        using var fixture = new Fixture();
        var first = new JsonPdfLayOutPresetStore(fixture.Path);
        var second = new JsonPdfLayOutPresetStore(fixture.Path);
        await Task.WhenAll(first.AddAsync("方案一", new()), second.AddAsync("方案二", new()));
        Assert.AreEqual(3, (await first.LoadAsync()).Count);
        const string damaged = "{ invalid json";
        await File.WriteAllTextAsync(fixture.Path, damaged);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => first.LoadAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.AddAsync("方案三", new()));
        Assert.AreEqual(damaged, await File.ReadAllTextAsync(fixture.Path));
    }

    [TestMethod]
    public async Task ViewModel_LoadsAllSettingsInOneLayoutUpdateAndKeepsPreviewOptionsSeparate()
    {
        using var fixture = new Fixture();
        var store = new JsonPdfLayOutPresetStore(fixture.Path);
        PdfLayOutPreset saved = await store.AddAsync("横向拼版", SampleSettings());
        var model = new PdfLayOutViewModel(null, null, null, presetStore: store);
        await model.InitializePresetsAsync();
        Assert.IsTrue(model.SelectedPreset!.IsDefault);
        Assert.IsFalse(model.DeletePresetCommand.CanExecute(null));
        model.SetSourceDocument(new("source.pdf", [new(0, new(95, 52))]));
        model.ShowPageContent = false;
        int rebuilds = 0;
        model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(model.CurrentLayout)) rebuilds++; };
        model.SelectedPreset = model.Presets.Single(item => item.Id == saved.Id);
        Assert.AreEqual(1, rebuilds);
        Assert.AreEqual(SampleSettings(), model.CreateSettingsSnapshot());
        Assert.AreEqual(1, model.PaperSizeIndex);
        Assert.AreEqual(1, model.OrientationIndex);
        Assert.IsFalse(model.ShowPageContent);
        Assert.IsTrue(model.CurrentLayout!.IsValid);

        model.HorizontalGap = 9;
        await model.RenamePresetCommand.ExecuteAsync("更名");
        Assert.AreEqual(7d, (await store.LoadAsync()).Single(item => item.Id == saved.Id).Settings.HorizontalGap);
        await model.SavePresetCommand.ExecuteAsync(null);
        Assert.AreEqual(9d, (await store.LoadAsync()).Single(item => item.Id == saved.Id).Settings.HorizontalGap);
        await model.DeletePresetCommand.ExecuteAsync(null);
        Assert.IsTrue(model.SelectedPreset!.IsDefault);
        Assert.AreEqual(new PdfLayOutSettings(), model.CreateSettingsSnapshot());
        Assert.IsFalse(model.DeletePresetCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ViewModel_BlocksSwitchUntilSavedAndDoesNotTreatViewChangesAsPresetEdits()
    {
        using var fixture = new Fixture();
        var store = new JsonPdfLayOutPresetStore(fixture.Path);
        PdfLayOutPreset saved = await store.AddAsync("标签", SampleSettings());
        var model = new PdfLayOutViewModel(null, null, null, presetStore: store);
        await model.InitializePresetsAsync();
        PdfLayOutPreset target = model.Presets.Single(item => item.Id == saved.Id);
        PdfLayOutPreset original = model.SelectedPreset!;
        model.ShowPageContent = false;
        model.SelectedSection = 2;
        model.SetSourceDocument(new("source.pdf", [new(0, new(95, 52))]));
        Assert.IsFalse(model.HasUnsavedPresetChanges);

        model.HorizontalGap = 9;
        Assert.IsTrue(model.HasUnsavedPresetChanges);
        Assert.IsFalse(model.TrySelectPreset(target));
        Assert.AreSame(original, model.SelectedPreset);
        Assert.AreEqual(9d, model.HorizontalGap);
        StringAssert.Contains(model.ErrorMessage!, "未保存");
        await model.RefreshPresetsCommand.ExecuteAsync(null);
        Assert.AreEqual(9d, model.HorizontalGap);
        await model.RenamePresetCommand.ExecuteAsync("默认改名");
        Assert.IsTrue(model.HasUnsavedPresetChanges, "重命名不能隐式保存设置。");
        await model.SavePresetCommand.ExecuteAsync(null);
        Assert.IsFalse(model.HasUnsavedPresetChanges);
        Assert.IsTrue(model.TrySelectPreset(target));
        Assert.AreEqual(SampleSettings(), model.CreateSettingsSnapshot());
        Assert.IsFalse(model.HasUnsavedPresetChanges);
        model.HorizontalGap = 10;
        model.HorizontalGap = SampleSettings().HorizontalGap;
        Assert.IsFalse(model.HasUnsavedPresetChanges, "还原参数后不应再阻止切换。");
    }

    private static PdfLayOutSettings SampleSettings() => new()
    {
        SheetSize = new(420, 297), SheetMargins = new(11, 12, 13, 14), SourceCrop = new(1, 2, 3, 4),
        Offset = new(-2, 3), GridMode = PdfLayOutGridMode.Manual, Columns = 3, Rows = 4,
        HorizontalGap = 7, VerticalGap = 8, ScaleMode = PdfLayOutScaleMode.Custom, ScalePercent = 125,
        Marks = new() { ShowCropMarks = false, ShowInnerMarks = false, Length = 3.5, Offset = 1.5, ShowCellBorders = true },
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BigDvToolBox-Presets-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "presets.json");
        public Fixture() => Directory.CreateDirectory(_directory);
        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(_directory)) File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}
