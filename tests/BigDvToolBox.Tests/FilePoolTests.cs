using BigDvToolBox_App.Features.FilePool;
using BigDvToolBox_App.Features.BatchRename;
using BigDvToolBox_App.Features.ImageProcessing;
using BigDvToolBox_App.Features.PdfOutlineFonts;
using BigDvToolBox_App.Features.RenameByOCR;
using BigDvToolBox.Renaming.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class FilePoolTests
{
    [TestMethod]
    public async Task Import_NormalizesDeduplicatesAndDoesNotCollectDirectories()
    {
        using var disk = new Disk();
        string a = disk.File("a.pdf"), b = disk.File("b.png");
        var service = new FilePoolService();
        var result = await service.ImportAsync([a, Path.Combine(disk.Root, ".", "a.pdf"), b, a.ToUpperInvariant()], false, default);
        Assert.AreEqual(2, result.Added); Assert.AreEqual(2, result.Duplicates);
        CollectionAssert.AreEqual(new[] { a, b }, service.Entries.Select(e => e.Path).ToArray());
        Assert.IsNull(service.AddFile(disk.Root));
        var id = service.Entries[0].Id;
        Assert.AreEqual(id, service.AddFile(a)!.Id);
    }

    [TestMethod]
    public async Task FolderScan_DefaultIsDirect_RecursiveIncludesDescendants_ContinuesAfterMissingInput()
    {
        using var disk = new Disk();
        string a = disk.File("a.txt"), b = disk.File("child/b.pdf");
        var service = new FilePoolService();
        var direct = await service.ImportAsync([disk.Root], false, default);
        Assert.AreEqual(1, direct.Added); Assert.AreEqual(a, service.Entries[0].Path);
        var recursive = await service.ImportAsync([disk.Root, Path.Combine(disk.Root, "missing")], true, default);
        Assert.AreEqual(1, recursive.Added); Assert.AreEqual(1, recursive.Duplicates); Assert.AreEqual(1, recursive.Skipped);
        Assert.IsNotNull(service.Find(b));
        Assert.IsTrue(service.Entries.All(e => File.Exists(e.Path)));
    }

    [TestMethod]
    public async Task ScanCancellation_PreservesAlreadyImportedFiles()
    {
        using var disk = new Disk();
        for (int i = 0; i < 300; i++) disk.File($"{i}.txt");
        using var cancel = new CancellationTokenSource();
        int handled = 0;
        var service = new FilePoolService(action =>
        {
            action();
            if (++handled == 1) cancel.Cancel();
            return Task.CompletedTask;
        });
        var result = await service.ImportAsync([disk.Root], true, cancel.Token);
        Assert.IsTrue(result.Cancelled); Assert.AreEqual(128, result.Added); Assert.HasCount(128, service.Entries);
    }

    [TestMethod]
    public async Task Refresh_MarksMissingAndSnapshotExcludesIt_RestoredFileKeepsIdentity()
    {
        using var disk = new Disk();
        string path = disk.File("a.pdf");
        var service = new FilePoolService(); var entry = service.AddFile(path)!;
        File.Delete(path); await service.RefreshAsync();
        Assert.IsFalse(entry.Exists); Assert.HasCount(0, service.Snapshot([entry.Id]));
        disk.File("a.pdf"); await service.RefreshAsync();
        Assert.IsTrue(entry.Exists); Assert.AreEqual(entry.Id, service.Snapshot([entry.Id])[0].Id);
    }

    [TestMethod]
    public void RenameSwap_IsAtomic_AndDirectoryMappingDoesNotMatchSiblingPrefix()
    {
        using var disk = new Disk();
        string a = disk.File("a.txt"), b = disk.File("b.txt"), child = disk.File("dir/c.txt"), sibling = disk.File("dir2/d.txt");
        var service = new FilePoolService(); var first = service.AddFile(a)!; var second = service.AddFile(b)!;
        var nested = service.AddFile(child)!; service.AddFile(sibling);
        service.ApplyRenames([new(a, b), new(b, a), new(Path.Combine(disk.Root, "dir"), Path.Combine(disk.Root, "renamed"), true)]);
        Assert.AreEqual(b, first.Path); Assert.AreEqual(a, second.Path);
        Assert.AreEqual(Path.Combine(disk.Root, "renamed", "c.txt"), nested.Path);
        Assert.IsNotNull(service.Find(sibling)); Assert.HasCount(4, service.Entries);
    }

    [TestMethod]
    public void Leases_RejectSameFileAndAncestorConflicts_AllowSiblingAndOwnerReentry_ReleaseOnDispose()
    {
        using var disk = new Disk();
        string a = disk.File("dir/a.pdf"), b = disk.File("dir2/b.pdf");
        var service = new FilePoolService(); object owner = new(), other = new();
        Assert.IsTrue(service.TryAcquire(owner, [new(a)], out var source));
        using (source)
        {
            Assert.IsFalse(service.TryAcquire(other, [new(a)], out _));
            Assert.IsFalse(service.TryAcquire(other, [new(Path.GetDirectoryName(a)!, true)], out _));
            Assert.IsTrue(service.TryAcquire(owner, [new(a)], out var reentrant)); reentrant!.Dispose();
            Assert.IsTrue(service.TryAcquire(other, [new(b)], out var sibling)); sibling!.Dispose();
        }
        Assert.IsTrue(service.TryAcquire(other, [new(a)], out var released)); released!.Dispose();
    }

    [TestMethod]
    public void PdfReplacement_DeduplicatesOutput_DoesNotDeleteDiskSource_AndUsesNewIdentity()
    {
        using var disk = new Disk(); string source = disk.File("a.pdf"), output = disk.File("a_outlined.pdf");
        var service = new FilePoolService(); var original = service.AddFile(source)!; var produced = service.AddFile(output)!;
        service.ReplacePdfOutput(source, output);
        Assert.HasCount(1, service.Entries); Assert.AreEqual(produced.Id, service.Entries[0].Id);
        Assert.AreNotEqual(original.Id, produced.Id); Assert.IsTrue(File.Exists(source));
        service.ReplacePdfOutput(output, Path.Combine(disk.Root, "not-created.pdf"));
        Assert.HasCount(1, service.Entries);
    }

    [TestMethod]
    public async Task Modules_ImportAutoTracks_RemovePoolDoesNotRemoveTasks_AndImageOutputsStayOut()
    {
        using var disk = new Disk(); string path = disk.File("a.png");
        var service = new FilePoolService();
        var image = new ImageProcessingViewModel(new Resize(), new Settings()); image.ConnectFilePool(service);
        image.AddFiles([path]); var originalId = service.Entries[0].Id;
        Assert.AreEqual(originalId, image.Files[0].PoolId);
        service.Remove([originalId]); Assert.HasCount(1, image.Files); Assert.HasCount(0, service.Entries);
        service.AddFile(path);
        await image.StartProcessingAsync();
        Assert.HasCount(1, service.Entries); Assert.AreEqual(path, service.Entries[0].Path);
        Assert.IsNotNull(image.Files[0].OutputPath); Assert.IsTrue(File.Exists(image.Files[0].OutputPath));
        image.ConnectFilePool(null);
    }

    [TestMethod]
    public async Task PdfConversion_ReplacesOnlySuccessfulInputs_LeavesModuleTasksIndependent()
    {
        using var disk = new Disk(); string a = disk.File("a.pdf"), b = disk.File("b.pdf"), c = disk.File("c.pdf");
        var service = new FilePoolService();
        var pdf = new PdfOutlineFontsViewModel(new Ghostscript(b, c)); pdf.ConnectFilePool(service);
        pdf.AddFiles([a, b, c]); await pdf.InitializeAsync();
        var result = await pdf.StartConversionAsync();
        Assert.AreEqual(1, result.Succeeded); Assert.AreEqual(1, result.Failed); Assert.AreEqual(1, result.Cancelled);
        Assert.IsNull(service.Find(a)); Assert.IsNotNull(service.Find(PdfOutlineFileRules.GetOutputPath(a, null)));
        Assert.IsNotNull(service.Find(b)); Assert.IsNotNull(service.Find(c)); Assert.AreEqual(a, pdf.Files[0].FilePath);
        Assert.IsTrue(File.Exists(a)); pdf.ConnectFilePool(null);
    }

    [TestMethod]
    public async Task BatchRename_UpdatesOtherModulePathsAndPoolIdentity_AndDetachesOnClose()
    {
        using var disk = new Disk(); string path = disk.File("a.png");
        var service = new FilePoolService();
        var image = new ImageProcessingViewModel(new Resize(), new Settings()); image.ConnectFilePool(service); image.AddFiles([path]);
        var rename = new BatchRenameViewModel(); rename.ConnectFilePool(service);
        try
        {
            await rename.AddPathsAsync([path]); var id = service.Entries[0].Id;
            rename.AddMethod(RenameMethod.Insert); rename.Methods[0].Text = "new_";
            await rename.PreviewAsync(); await rename.ExecuteCommand.ExecuteAsync(null);
            string renamed = Path.Combine(disk.Root, "new_a.png");
            Assert.AreEqual(renamed, image.Files[0].FilePath); Assert.AreEqual(id, service.Find(renamed)!.Id);
            image.ConnectFilePool(null);
            string moved = Path.Combine(disk.Root, "again.png"); File.Move(renamed, moved);
            service.ApplyRenames([new(renamed, moved)]);
            Assert.AreEqual(renamed, image.Files[0].FilePath);
        }
        finally { image.ConnectFilePool(null); rename.ConnectFilePool(null); rename.Cancel(); }
    }

    [TestMethod]
    public async Task FolderRename_UpdatesPooledDescendantAndOtherModuleTask()
    {
        using var disk = new Disk(); string path = disk.File("dir/a.png");
        var service = new FilePoolService();
        var image = new ImageProcessingViewModel(new Resize(), new Settings()); image.ConnectFilePool(service); image.AddFiles([path]);
        var rename = new BatchRenameViewModel { ActiveTabIndex = 1 }; rename.ConnectFilePool(service);
        try
        {
            await rename.AddPathsAsync([Path.GetDirectoryName(path)!]);
            rename.AddMethod(RenameMethod.Insert); rename.Methods[0].Text = "new_";
            await rename.PreviewAsync(); await rename.ExecuteCommand.ExecuteAsync(null);
            string target = Path.Combine(disk.Root, "new_dir", "a.png");
            Assert.AreEqual(target, image.Files[0].FilePath); Assert.IsNotNull(service.Find(target));
            Assert.HasCount(1, service.Entries);
        }
        finally { image.ConnectFilePool(null); rename.ConnectFilePool(null); rename.Cancel(); }
    }

    [TestMethod]
    public async Task ReservedInput_StopsModuleExecutionWithoutChangingFile()
    {
        using var disk = new Disk(); string path = disk.File("a.png");
        var service = new FilePoolService(); var resize = new Resize();
        var image = new ImageProcessingViewModel(resize, new Settings()); image.ConnectFilePool(service); image.AddFiles([path]);
        Assert.IsTrue(service.TryAcquire(new object(), [new(path)], out var active));
        using (active) { await image.StartProcessingAsync(); Assert.AreEqual(0, resize.Calls); Assert.IsTrue(image.BatchStatus.Contains("其他模块")); }
        await image.StartProcessingAsync(); Assert.AreEqual(1, resize.Calls); image.ConnectFilePool(null);
    }

    [TestMethod]
    public async Task Ocr_AppendAcrossFolders_RenameUpdatesLoadedSetWithoutRescanningFolder()
    {
        using var disk = new Disk(); string a = disk.File("first/a.pdf"), b = disk.File("second/b.png");
        var service = new FilePoolService(); using var ocr = new RenameByOcrViewModel(new Renderer()); ocr.ConnectFilePool(service);
        var input = await ocr.ImportPoolFilesAsync([a, b, a, disk.File("skip.txt")]);
        Assert.AreEqual(2, input.Added); Assert.AreEqual(1, input.Duplicates); Assert.AreEqual(1, input.Skipped);
        ocr.SetSelectedRegion(new(.1, .2, .3, .4));
        ocr.Results.Add(new RenamePreviewItem { OriginalPath = a, TargetPath = Path.Combine(Path.GetDirectoryName(a)!, "renamed.pdf") });
        var result = await ocr.ApplyRenamesAsync();
        Assert.AreEqual(1, result.Succeeded); Assert.HasCount(2, ocr.Files);
        Assert.AreEqual("renamed.pdf", Path.GetFileName(ocr.Files[0])); Assert.AreEqual(b, ocr.Files[1]);
        Assert.AreEqual(.3, ocr.SelectedRegion.Width); Assert.IsNotNull(service.Find(ocr.Files[0]));
        // A later folder load still replaces the explicit collection.
        await ocr.LoadFolderAsync(Path.GetDirectoryName(b)!);
        Assert.HasCount(1, ocr.Files); Assert.AreEqual(b, ocr.Files[0]);
    }

    [TestMethod]
    public async Task Panel_FilteringSelectionAndBusyTarget_AreIndependentOfTaskLists()
    {
        using var disk = new Disk(); string a = disk.File("a.pdf"), b = disk.File("b.png");
        var service = new FilePoolService(); service.AddFile(a); service.AddFile(b);
        var state = new TargetState(); var received = new List<string>();
        var target = new FilePoolTarget("PDF", state, () => !state.Busy, p => Path.GetExtension(p) == ".pdf",
            paths => { received.AddRange(paths); return Task.FromResult(new FilePoolImportResult(paths.Count, 0, 0)); },
            _ => { }, () => state.Busy, () => { });
        using var vm = new FilePoolViewModel(); vm.Connect(service); vm.SetTarget(target);
        Assert.HasCount(1, vm.VisibleFiles); vm.Select([service.Find(a)!.Id]); Assert.IsTrue(vm.CanSend);
        state.SetBusy(true); Assert.IsFalse(vm.CanSend); state.SetBusy(false);
        Assert.IsTrue(await vm.SendAsync()); CollectionAssert.AreEqual(new[] { a }, received);
        vm.OnlyCompatible = false; Assert.HasCount(2, vm.VisibleFiles);
        vm.SearchText = "b"; Assert.HasCount(1, vm.VisibleFiles); Assert.AreEqual(b, vm.VisibleFiles[0].Path);
        vm.SearchText = ""; vm.TypeIndex = 1; Assert.HasCount(1, vm.VisibleFiles);
        vm.SetTarget(null); Assert.IsFalse(vm.CanSend);
    }

    [TestMethod]
    public async Task Ocr_CancellationAfterFirstRename_StillPublishesSuccessfulPath()
    {
        using var disk = new Disk(); string a = disk.File("a.pdf"), b = disk.File("b.pdf");
        var service = new FilePoolService(); using var ocr = new RenameByOcrViewModel(new Renderer()); ocr.ConnectFilePool(service);
        await ocr.ImportPoolFilesAsync([a, b]);
        var first = new RenamePreviewItem { OriginalPath = a, TargetPath = Path.Combine(disk.Root, "new_a.pdf") };
        var second = new RenamePreviewItem { OriginalPath = b, TargetPath = Path.Combine(disk.Root, "new_b.pdf") };
        ocr.Results.Add(first); ocr.Results.Add(second);
        first.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(first.Status) && first.Status == "已重命名") ocr.Cancel(); };
        await ocr.ApplyRenamesAsync();
        Assert.AreEqual(first.TargetPath, ocr.Files[0]); Assert.AreEqual(b, ocr.Files[1]);
        Assert.IsNotNull(service.Find(first.TargetPath)); Assert.IsNull(service.Find(a)); Assert.IsNotNull(service.Find(b));
        StringAssert.Contains(ocr.Status, "已取消");
    }

    [TestMethod]
    public async Task BatchRename_SwapPaths_PublishesOnceAndKeepsIndependentIdentities()
    {
        using var disk = new Disk(); string a = disk.File("a.png"), b = disk.File("b.png");
        var service = new FilePoolService();
        var image = new ImageProcessingViewModel(new Resize(), new Settings()); image.ConnectFilePool(service); image.AddFiles([a, b]);
        var rename = new BatchRenameViewModel(); rename.ConnectFilePool(service);
        try
        {
            var firstId = service.Find(a)!.Id; var secondId = service.Find(b)!.Id;
            await rename.AddPathsAsync([a, b]);
            rename.ApplyPreset(new RenamePreset(RenamePreset.CurrentVersion, "swap", [
                new RenameRule { Method = RenameMethod.Replace, Text = "a", Replacement = "temporary" },
                new RenameRule { Method = RenameMethod.Replace, Text = "b", Replacement = "a" },
                new RenameRule { Method = RenameMethod.Replace, Text = "temporary", Replacement = "b" },
            ]));
            await rename.PreviewAsync(); await rename.ExecuteCommand.ExecuteAsync(null);
            Assert.AreEqual(b, rename.Files[0].CurrentPath); Assert.AreEqual(a, rename.Files[1].CurrentPath);
            Assert.AreEqual(b, image.Files[0].FilePath); Assert.AreEqual(a, image.Files[1].FilePath);
            Assert.AreEqual(firstId, service.Find(b)!.Id); Assert.AreEqual(secondId, service.Find(a)!.Id);
            Assert.AreEqual("已重命名", rename.Files[0].Status);
        }
        finally { image.ConnectFilePool(null); rename.ConnectFilePool(null); rename.Cancel(); }
    }

    [TestMethod]
    public async Task BatchRename_TargetAppearsAfterPreview_RestoredPathsRemainInPool()
    {
        using var disk = new Disk(); string a = disk.File("a.txt");
        var service = new FilePoolService(); var rename = new BatchRenameViewModel(); rename.ConnectFilePool(service);
        try
        {
            await rename.AddPathsAsync([a]); rename.AddMethod(RenameMethod.Insert); rename.Methods[0].Text = "new_";
            await rename.PreviewAsync(); disk.File("new_a.txt");
            await rename.ExecuteCommand.ExecuteAsync(null);
            Assert.AreEqual(a, rename.Files[0].CurrentPath); Assert.IsNotNull(service.Find(a));
            Assert.HasCount(1, service.Entries); Assert.IsTrue(File.Exists(a));
        }
        finally { rename.ConnectFilePool(null); rename.Cancel(); }
    }

    private sealed class Disk : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "BigDvFilePoolTests", Guid.NewGuid().ToString("N"));
        public Disk() => Directory.CreateDirectory(Root);
        public string File(string name)
        {
            string path = Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, "sample"); return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class Renderer : IDocumentRenderer
    {
        public Task<RenderedDocument> RenderAsync(string path, int pdfDpi, CancellationToken cancellationToken) => Task.FromResult(new RenderedDocument(path, [1], 100, 100, pdfDpi));
        public Task<byte[]> CropAsync(RenderedDocument document, NormalizedOcrRegion region, CancellationToken cancellationToken) => Task.FromResult(document.PngBytes);
    }
    private sealed class Resize : IImageResizeService
    {
        public int Calls;
        public Task<ImageResizeResult> ResizeAsync(string inputPath, string outputPath, ImageResizeOptions options, CancellationToken cancellationToken)
        { Calls++; File.WriteAllText(outputPath, "output"); return Task.FromResult(new ImageResizeResult(10, 10, outputPath)); }
    }
    private sealed class Settings : IImageProcessingSettingsStore
    {
        public ImageProcessingDefaults Load() => new();
        public void SaveLongEdge(int value) { }
        public void SaveOutputDirectory(string? value) { }
        public void SaveConflictPolicy(ImageConflictPolicy value) { }
    }
    private sealed class Ghostscript(string failed, string cancelled) : IGhostscriptService
    {
        public bool IsSupported => true;
        public string? VersionText => "test";
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ConvertAsync(string inputPath, string outputPath, PdfConvertMode convertMode, int dpi, CancellationToken cancellationToken)
        {
            if (inputPath == failed) throw new IOException("test failure");
            if (inputPath == cancelled) throw new OperationCanceledException();
            File.WriteAllText(outputPath, "outlined"); return Task.CompletedTask;
        }
    }
    private sealed class TargetState : ObservableObject
    {
        public bool Busy { get; private set; }
        public void SetBusy(bool busy) { Busy = busy; OnPropertyChanged(nameof(Busy)); }
    }
}
