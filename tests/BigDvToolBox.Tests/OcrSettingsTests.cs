using BigDvToolBox_App.Features.RenameByOCR;
using Tesseract;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class OcrSettingsTests
{
    [TestMethod]
    public void DefaultModels_AreSeededOnceWithoutOverwritingUserFiles()
    {
        using var env = new OcrSettingsTestEnvironment();
        Directory.CreateDirectory(env.Models.DefaultDirectory);
        string english = Path.Combine(env.Models.DefaultDirectory, "eng.traineddata");
        File.WriteAllText(english, "user model");
        env.Models.EnsureDefaultModels();
        Assert.AreEqual("user model", File.ReadAllText(english));
        Assert.HasCount(4, env.Models.Scan(env.Models.DefaultDirectory));
        File.Delete(english);
        env.Models.EnsureDefaultModels();
        Assert.IsFalse(File.Exists(english), "Refresh must not restore a deliberately removed model.");
    }

    [TestMethod]
    public void Catalog_DiscoversTopLevelLanguagesAndFiltersAuxiliaryModels()
    {
        using var env = new OcrSettingsTestEnvironment();
        string path = env.CreateDirectory("custom");
        File.WriteAllText(Path.Combine(path, "chi_sim.traineddata"), "model");
        File.WriteAllText(Path.Combine(path, "custom_lang.traineddata"), "model");
        File.WriteAllText(Path.Combine(path, "osd.traineddata"), "auxiliary");
        File.WriteAllText(Path.Combine(path, "equ.traineddata"), "auxiliary");
        File.WriteAllText(Path.Combine(path, "eng.traineddata"), "");
        File.WriteAllText(Path.Combine(path, "a+b.traineddata"), "not a language code");
        string nested = env.CreateDirectory("custom/nested");
        File.WriteAllText(Path.Combine(nested, "rus.traineddata"), "nested");
        IReadOnlyList<OcrModelInfo> models = env.Models.Scan(path);
        Assert.HasCount(3, models);
        Assert.IsFalse(models.Single(m => m.Code == "eng").Available);
        Assert.IsTrue(models.Single(m => m.Code == "chi_sim").Available);
    }

    [TestMethod]
    public void Settings_RoundTripOrderedLanguagesAndRecoverFromCorruptJson()
    {
        using var env = new OcrSettingsTestEnvironment();
        var settings = new OcrSettings
        {
            ModelDirectory = env.Root,
            SelectedLanguages = ["rus", "eng"],
            Layout = OcrLayout.SingleLine,
            DisableDictionaries = true,
        };
        env.Store.Save(settings);
        OcrSettings actual = env.Store.Load();
        CollectionAssert.AreEqual(settings.SelectedLanguages, actual.SelectedLanguages);
        Assert.AreEqual(settings.ModelDirectory, actual.ModelDirectory);
        Assert.AreEqual(settings.Layout, actual.Layout);
        Assert.IsTrue(actual.DisableDictionaries);
        File.WriteAllText(env.SettingsPath, "{bad json");
        CollectionAssert.AreEqual(new OcrSettings().SelectedLanguages, env.Store.Load().SelectedLanguages);
        Assert.IsFalse(env.Store.Load().DisableDictionaries);
    }

    [TestMethod]
    public async Task Refresh_PreservesSelectionAndEditedResultsAndDoesNotSelectNewModels()
    {
        using var env = new OcrSettingsTestEnvironment();
        using var vm = env.CreateViewModel();
        await vm.InitializeOcrSettingsAsync();
        Assert.IsTrue(vm.IsOcrReady);
        Assert.AreEqual("eng + ces + pol + rus", vm.LanguageSummary);
        var row = new RenamePreviewItem { OriginalPath = Path.Combine(env.Root, "source.pdf"), TargetName = "manual.pdf" };
        vm.Results.Add(row);
        File.WriteAllText(Path.Combine(env.Models.DefaultDirectory, "jpn.traineddata"), "new");
        await vm.RefreshLanguagesAsync();
        Assert.IsFalse(vm.Languages.Single(l => l.Code == "jpn").IsSelected);
        vm.Languages.Single(l => l.Code == "eng").IsSelected = false;
        Assert.AreSame(row, vm.Results.Single());
        Assert.AreEqual("manual.pdf", row.TargetName);
        CollectionAssert.AreEqual(new[] { "ces", "pol", "rus" }, env.Store.Load().SelectedLanguages);
        using var reopened = env.CreateViewModel();
        await reopened.InitializeOcrSettingsAsync();
        Assert.AreEqual("ces + pol + rus", reopened.LanguageSummary);
    }

    [TestMethod]
    public async Task CustomDirectory_DoesNotFallBackAndMissingSelectionsCanBeRemoved()
    {
        using var env = new OcrSettingsTestEnvironment();
        using var vm = env.CreateViewModel();
        await vm.InitializeOcrSettingsAsync();
        string custom = env.CreateDirectory("custom");
        File.WriteAllText(Path.Combine(custom, "eng.traineddata"), "only english");
        await vm.SetModelDirectoryAsync(custom);
        Assert.IsFalse(vm.IsOcrReady);
        Assert.IsTrue(vm.Languages.Single(l => l.Code == "rus").IsSelected);
        Assert.IsFalse(vm.Languages.Single(l => l.Code == "rus").IsAvailable);
        foreach (var language in vm.Languages.Where(l => l.Code != "eng")) language.IsSelected = false;
        Assert.IsTrue(vm.IsOcrReady);
        vm.Languages.Single(l => l.Code == "eng").IsSelected = false;
        Assert.IsFalse(vm.IsOcrReady);
        vm.Languages.Single(l => l.Code == "eng").IsSelected = true;
        File.Delete(Path.Combine(custom, "eng.traineddata"));
        await vm.RefreshLanguagesAsync();
        Assert.IsFalse(vm.IsOcrReady);
        await vm.SetModelDirectoryAsync(Path.Combine(env.Root, "absent"));
        StringAssert.Contains(vm.OcrConfigurationError, "不可访问");
    }

    [TestMethod]
    public async Task SaveFailure_KeepsSessionChoicesAndReportsFailure()
    {
        using var env = new OcrSettingsTestEnvironment();
        using var vm = env.CreateViewModel(store: new FailingStore());
        await vm.InitializeOcrSettingsAsync();
        vm.DisableDictionaries = true;
        Assert.IsTrue(vm.DisableDictionaries);
        StringAssert.Contains(vm.SettingsSaveError, "保存失败");
        Assert.IsTrue(vm.IsOcrReady);
    }

    [TestMethod]
    public async Task TaskSnapshot_UsesSelectedOptionsAndCsvKeepsAutoLayout()
    {
        using var env = new OcrSettingsTestEnvironment();
        var engine = new CapturingOcr();
        OcrEngineOptions? captured = null;
        using var vm = env.CreateViewModel(options => { captured = options; return engine; });
        await vm.InitializeOcrSettingsAsync();
        string files = env.CreateDirectory("files");
        File.WriteAllText(Path.Combine(files, "test.pdf"), "test");
        await vm.LoadFolderAsync(files);
        vm.SetSelectedRegion(new(0, 0, 1, 1));
        vm.LayoutIndex = (int)OcrLayout.SingleLine;
        vm.DisableDictionaries = true;
        vm.Languages.Single(l => l.Code == "ces").IsSelected = false;
        await vm.GenerateRenamePreviewAsync();
        Assert.IsNotNull(captured);
        Assert.AreEqual("eng+pol+rus", captured.Languages);
        Assert.AreEqual(env.Models.DefaultDirectory, captured.ModelDirectory);
        Assert.IsTrue(captured.DisableDictionaries);
        Assert.AreEqual(OcrSegmentationMode.SingleLine, engine.LastMode);
        await vm.ExportCsvAsync(Path.Combine(env.Root, "export.csv"));
        Assert.AreEqual(OcrSegmentationMode.Auto, engine.LastMode);

        int calls = engine.Calls;
        foreach (var language in vm.Languages) language.IsSelected = false;
        Assert.IsFalse(vm.CanStart);
        Assert.IsFalse(vm.CanExport);
        await vm.GenerateRenamePreviewAsync();
        await vm.ExportCsvAsync(Path.Combine(env.Root, "invalid.csv"));
        Assert.AreEqual(calls, engine.Calls);
        Assert.IsFalse(File.Exists(Path.Combine(env.Root, "invalid.csv")));
    }

    [TestMethod]
    public void LayoutDefaults_MatchExistingTaskBehavior()
    {
        Assert.AreEqual(OcrSegmentationMode.SingleWord, RenameByOcrViewModel.ResolveLayout(OcrLayout.FollowTask, RenameMode.FixedRegion));
        Assert.AreEqual(OcrSegmentationMode.Auto, RenameByOcrViewModel.ResolveLayout(OcrLayout.FollowTask, RenameMode.AnchorText));
        Assert.AreEqual(OcrSegmentationMode.SingleLine, RenameByOcrViewModel.ResolveLayout(OcrLayout.SingleLine, RenameMode.FixedRegion));
    }

    [TestMethod]
    public void NativeEngine_UsesLstmAndConstructorDictionaryOptionsWithAdditionalLanguage()
    {
        using var env = new OcrSettingsTestEnvironment();
        string data = Path.Combine(AppContext.BaseDirectory, "Features", "RenameByOCR", "tessdata");
        string custom = env.CreateDirectory("real-models");
        // A real additional Latin model, packaged only with this test's temporary directory.
        File.Copy(Path.Combine(data, "eng.traineddata"), Path.Combine(custom, "extra.traineddata"));
        foreach (bool disable in new[] { false, true })
        {
            using var ocr = new TesseractOcrService(new(custom, "extra", disable));
            Assert.AreEqual(!disable, ocr.GetBooleanVariable("load_system_dawg"));
            Assert.AreEqual(!disable, ocr.GetBooleanVariable("load_freq_dawg"));
        }
    }

    [TestMethod]
    public void ExternalRealLanguageModel_CanBeDiscoveredAndLoaded()
    {
        string? model = Environment.GetEnvironmentVariable("BIGDV_OCR_EXTRA_MODEL");
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
            Assert.Inconclusive("Set BIGDV_OCR_EXTRA_MODEL to run the external real language integration test.");
        using var env = new OcrSettingsTestEnvironment();
        string directory = env.CreateDirectory("extra-model");
        string code = Path.GetFileNameWithoutExtension(model);
        File.Copy(model, Path.Combine(directory, Path.GetFileName(model)));
        Assert.AreEqual(code, env.Models.Scan(directory).Single().Code);
        using var engine = new TesseractOcrService(new(directory, code, false));
        Assert.AreEqual(true, engine.GetBooleanVariable("load_system_dawg"));
    }

    [TestMethod]
    public void BrokenSecondaryModel_IsNotSilentlyIgnoredByNativeEngine()
    {
        using var env = new OcrSettingsTestEnvironment();
        string directory = env.CreateDirectory("mixed-models");
        string data = Path.Combine(AppContext.BaseDirectory, "Features", "RenameByOCR", "tessdata");
        File.Copy(Path.Combine(data, "eng.traineddata"), Path.Combine(directory, "eng.traineddata"));
        File.WriteAllText(Path.Combine(directory, "bad.traineddata"), "corrupt");
        InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() =>
        {
            using var engine = new TesseractOcrService(new(directory, "eng+bad", false));
        });
        StringAssert.Contains(error.Message, "bad");
    }

    [TestMethod]
    public async Task RunningTask_DisablesConfigurationAndRetainsItsSnapshot()
    {
        using var env = new OcrSettingsTestEnvironment();
        var engine = new BlockingOcr();
        OcrEngineOptions? snapshot = null;
        using var vm = env.CreateViewModel(options => { snapshot = options; return engine; });
        await vm.InitializeOcrSettingsAsync();
        string files = env.CreateDirectory("files");
        File.WriteAllText(Path.Combine(files, "test.pdf"), "test");
        await vm.LoadFolderAsync(files);
        vm.SetSelectedRegion(new(0, 0, 1, 1));
        Task operation = vm.GenerateRenamePreviewAsync();
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.IsFalse(vm.CanConfigure);
            Assert.IsFalse(vm.CanStart);
            Assert.IsFalse(vm.CanExport);
            string originalDirectory = vm.ModelDirectory;
            await vm.SetModelDirectoryAsync(env.CreateDirectory("ignored"));
            Assert.AreEqual(originalDirectory, vm.ModelDirectory);
            Assert.AreEqual("eng+ces+pol+rus", snapshot!.Languages);
        }
        finally
        {
            engine.Continue.SetResult();
            await operation;
        }
        Assert.IsTrue(vm.CanConfigure);
    }

    [TestMethod]
    public async Task InvalidNativeModel_FailsBeforeAnyBatchDocumentIsRendered()
    {
        using var env = new OcrSettingsTestEnvironment();
        var renderer = new CountingRenderer();
        using var vm = new RenameByOcrViewModel(renderer,
            options => new TesseractOcrService(options), env.Store, env.Models);
        await vm.InitializeOcrSettingsAsync();
        string files = env.CreateDirectory("files");
        File.WriteAllText(Path.Combine(files, "test.pdf"), "test");
        await vm.LoadFolderAsync(files);
        vm.SetSelectedRegion(new(0, 0, 1, 1));
        int before = renderer.Count;
        await vm.GenerateRenamePreviewAsync();
        Assert.IsTrue(vm.HasError);
        StringAssert.Contains(vm.Status, "OCR 处理失败");
        Assert.AreEqual(before, renderer.Count);
        Assert.HasCount(0, vm.Results);
    }

    private sealed class BlockingOcr : IOcrService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> RecognizeAsync(byte[] bytes, CancellationToken token,
            OcrSegmentationMode segmentationMode = OcrSegmentationMode.Auto)
        {
            Started.SetResult();
            await Continue.Task.WaitAsync(token);
            return "result";
        }
        public void Dispose() { }
    }

    private sealed class CountingRenderer : IDocumentRenderer
    {
        public int Count { get; private set; }
        public Task<RenderedDocument> RenderAsync(string path, int dpi, CancellationToken token)
        {
            Count++;
            return Task.FromResult(new RenderedDocument(path, [1], 100, 100, dpi));
        }
        public Task<byte[]> CropAsync(RenderedDocument document, NormalizedOcrRegion region, CancellationToken token) =>
            Task.FromResult(document.PngBytes);
    }

    private sealed class FailingStore : IOcrSettingsStore
    {
        public OcrSettings Load() => new();
        public void Save(OcrSettings settings) => throw new IOException("test write denied");
    }

    private sealed class CapturingOcr : IOcrService
    {
        public int Calls { get; private set; }
        public OcrSegmentationMode LastMode { get; private set; }
        public Task<string> RecognizeAsync(byte[] bytes, CancellationToken token,
            OcrSegmentationMode segmentationMode = OcrSegmentationMode.Auto)
        {
            Calls++;
            LastMode = segmentationMode;
            return Task.FromResult("result");
        }
        public void Dispose() { }
    }
}

internal sealed class OcrSettingsTestEnvironment : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "BigDvToolBox.OcrSettings.Tests", Guid.NewGuid().ToString("N"));
    public string SettingsPath => Path.Combine(Root, "ocr-settings.json");
    public JsonOcrSettingsStore Store { get; }
    public OcrModelDirectoryService Models { get; }

    public OcrSettingsTestEnvironment()
    {
        string bundled = CreateDirectory("bundled");
        foreach (string code in new OcrSettings().SelectedLanguages)
            File.WriteAllText(Path.Combine(bundled, code + ".traineddata"), "fake model");
        Store = new(SettingsPath);
        Models = new(Path.Combine(Root, "models"), bundled);
    }

    public string CreateDirectory(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
    public RenameByOcrViewModel CreateViewModel(Func<OcrEngineOptions, IOcrService>? factory = null, IOcrSettingsStore? store = null) =>
        new(new StubRenderer(), factory, store ?? Store, Models);
    public void Dispose() => Directory.Delete(Root, recursive: true);

    private sealed class StubRenderer : IDocumentRenderer
    {
        public Task<RenderedDocument> RenderAsync(string path, int dpi, CancellationToken token) =>
            Task.FromResult(new RenderedDocument(path, [1], 100, 100, dpi));
        public Task<byte[]> CropAsync(RenderedDocument document, NormalizedOcrRegion region, CancellationToken token) =>
            Task.FromResult(document.PngBytes);
    }
}
