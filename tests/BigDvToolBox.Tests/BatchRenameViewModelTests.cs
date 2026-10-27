using BigDvToolBox.Renaming.Core;
using BigDvToolBox_App.Features.BatchRename;

namespace BigDvToolBox.Tests;

[TestClass]
public sealed class BatchRenameViewModelTests
{
    [TestMethod]
    public async Task DragReorder_InvalidatesPreviewAndChangesExecutionOrder()
    {
        using var disk = new TestDirectory();
        var vm = new BatchRenameViewModel();
        try
        {
            vm.ApplyPreset(new RenamePreset(RenamePreset.CurrentVersion, "排序测试", [
                new RenameRule { Method = RenameMethod.Replace, Text = "a", Replacement = "b" },
                new RenameRule { Method = RenameMethod.Replace, Text = "b", Replacement = "c" },
            ]));
            await vm.AddPathsAsync([disk.File("a.txt")]);
            await vm.PreviewAsync(); Assert.AreEqual("c.txt", vm.Files[0].TargetName);
            var originalOrder = vm.Methods.Select(m => m.Id).ToArray();
            var dragged = vm.Methods[1];
            vm.Methods.Move(1, 0);
            vm.CompleteMethodReorder(dragged, originalOrder);
            Assert.IsTrue(vm.HasUnsavedChanges); Assert.IsFalse(vm.CanExecute);
            Assert.AreSame(dragged, vm.SelectedMethod); Assert.AreEqual(1, dragged.Order);
            await vm.PreviewAsync(); Assert.AreEqual("b.txt", vm.Files[0].TargetName);
            Assert.AreEqual(dragged.Id, RenamePreset.Parse(vm.SerializePreset("排序测试")).Rules[0].Id);
        }
        finally { vm.Cancel(); }
    }

    [TestMethod]
    public void Selection_TracksCurrentMethodWithoutChangingPreset()
    {
        var vm = new BatchRenameViewModel();
        try
        {
            vm.ApplyPreset(new RenamePreset(RenamePreset.CurrentVersion, "选中测试", [
                new RenameRule { Method = RenameMethod.Insert, Text = "a" },
                new RenameRule { Method = RenameMethod.Insert, Text = "b" },
            ]));
            var first = vm.Methods[0]; var second = vm.Methods[1];
            Assert.IsTrue(first.IsSelected); Assert.IsFalse(second.IsSelected);
            vm.SelectedMethod = second;
            Assert.IsFalse(first.IsSelected); Assert.IsTrue(second.IsSelected);
            Assert.IsFalse(vm.HasUnsavedChanges);
            vm.RemoveMethodCommand.Execute(null);
            Assert.IsFalse(second.IsSelected); Assert.IsTrue(first.IsSelected);
            Assert.AreSame(first, vm.SelectedMethod); Assert.HasCount(1, vm.Methods);
        }
        finally { vm.Cancel(); }
    }

    [TestMethod]
    public void RepeatedMethods_HaveIndependentParametersAndMoveAsInstances()
    {
        var vm = new BatchRenameViewModel();
        try
        {
            vm.AddMethod(RenameMethod.Replace); vm.Methods[0].Text = "a";
            vm.AddMethod(RenameMethod.Replace); vm.Methods[1].Text = "b";
            Assert.AreNotEqual(vm.Methods[0].Id, vm.Methods[1].Id);
            Assert.AreEqual("a", vm.Methods[0].Text);
            var selected = vm.SelectedMethod;
            vm.MoveUpCommand.Execute(null);
            Assert.AreSame(selected, vm.Methods[0]); Assert.AreEqual(1, vm.Methods[0].Order);
            Assert.IsFalse(vm.CanMoveUp); Assert.IsTrue(vm.CanMoveDown);
            Assert.IsTrue(vm.HasUnsavedChanges);
        }
        finally { vm.Cancel(); }
    }

    [TestMethod]
    public void InvalidNumber_DoesNotSilentlyTruncate()
    {
        var method = new RenameMethodViewModel(new RenameRule { Method = RenameMethod.Insert }) { Position = 1.5 };
        Assert.Throws<ArgumentException>(() => method.Snapshot());
        method.Position = double.NaN;
        Assert.Throws<ArgumentException>(() => method.Snapshot());
    }

    [TestMethod]
    public async Task Preview_RecomputesFromSourceAndInvalidatesImmediatelyOnEdit()
    {
        using var disk = new TestDirectory(); string a = disk.File("a.txt");
        var vm = new BatchRenameViewModel();
        try
        {
            vm.AddMethod(RenameMethod.Insert); vm.Methods[0].Text = "x";
            await vm.AddPathsAsync([a, a]); await vm.PreviewAsync();
            Assert.HasCount(1, vm.Files); Assert.AreEqual("xa.txt", vm.Files[0].TargetName);
            await vm.PreviewAsync(); Assert.AreEqual("xa.txt", vm.Files[0].TargetName);
            Assert.IsTrue(vm.CanExecute);
            vm.Methods[0].Text = "y";
            Assert.IsFalse(vm.CanExecute); Assert.AreEqual("", vm.Files[0].TargetName);
            await vm.PreviewAsync(); Assert.AreEqual("ya.txt", vm.Files[0].TargetName);
        }
        finally { vm.Cancel(); }
    }

    [TestMethod]
    public async Task Tabs_KeepSeparateEntriesAndSortPreferences()
    {
        using var disk = new TestDirectory(); string b = disk.File("b.txt"), a = disk.File("a.txt");
        string folder = Directory.CreateDirectory(Path.Combine(disk.Path, "data.old")).FullName;
        var vm = new BatchRenameViewModel();
        try
        {
            await vm.AddPathsAsync([b, a]); vm.SortIndex = 1;
            Assert.AreEqual("a.txt", vm.Files[0].Name);
            vm.ActiveTabIndex = 1; Assert.AreEqual(0, vm.SortIndex);
            await vm.AddPathsAsync([folder]); Assert.HasCount(1, vm.Folders);
            vm.ActiveTabIndex = 0; Assert.AreEqual(1, vm.SortIndex); Assert.HasCount(2, vm.Files);
            vm.SortIndex = 0; Assert.AreEqual("b.txt", vm.Files[0].Name);
        }
        finally { vm.Cancel(); }
    }

    [TestMethod]
    public async Task Numbering_UsesOnlyIncludedRowsAndExecutionUpdatesCurrentPaths()
    {
        using var disk = new TestDirectory(); string a = disk.File("a.txt"), b = disk.File("b.txt");
        var vm = new BatchRenameViewModel();
        try
        {
            vm.AddMethod(RenameMethod.Numbering); await vm.AddPathsAsync([a, b]); vm.Files[0].Included = false;
            await vm.PreviewAsync(); Assert.AreEqual("001_b.txt", vm.Files[1].TargetName);
            await vm.ExecuteCommand.ExecuteAsync(null);
            Assert.IsTrue(File.Exists(a)); Assert.IsTrue(File.Exists(Path.Combine(disk.Path, "001_b.txt")));
            Assert.AreEqual("001_b.txt", vm.Files[1].Name); Assert.AreEqual("已重命名", vm.Files[1].Status);
            Assert.AreEqual("b.txt", vm.Files[1].OriginalName);
            Assert.IsFalse(vm.CanExecute);
        }
        finally { vm.Cancel(); }
    }

    [TestMethod]
    public async Task Presets_SaveReloadAndKeepEditsIsolated()
    {
        using var disk = new TestDirectory();
        var vm = new BatchRenameViewModel(Path.Combine(disk.Path, "presets"));
        var other = new BatchRenameViewModel(Path.Combine(disk.Path, "presets"));
        try
        {
            vm.AddMethod(RenameMethod.Insert); vm.Methods[0].Text = "original";
            var preset = await vm.SavePresetAsync("测试预设"); Assert.IsFalse(vm.HasUnsavedChanges);
            vm.Methods[0].Text = "edited"; Assert.AreEqual("original", preset.Rules[0].Text);
            await other.LoadPresetsAsync(); Assert.HasCount(1, other.Presets);
            other.ApplyPreset(other.Presets[0]); Assert.AreEqual("original", other.Methods[0].Text);
            Assert.IsFalse(other.HasUnsavedChanges);
        }
        finally { vm.Cancel(); other.Cancel(); }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BigDvRenameVmTests", Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public string File(string name)
        { string path = System.IO.Path.Combine(Path, name); System.IO.File.WriteAllText(path, "data"); return path; }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
