using BigDvToolBox.Renaming.Core;

namespace BigDvToolBox.Renaming.Core.Tests;

[TestClass]
public sealed class RenamingTests
{
    [TestMethod]
    [DataRow(0, "x-x-x.txt")]
    [DataRow(1, "x-a-a.txt")]
    [DataRow(2, "a-x-a.txt")]
    [DataRow(4, "a-a-a.txt")]
    public void Replacement_SelectsOccurrence(int occurrence, string expected)
    {
        var rule = new RenameRule { Method = RenameMethod.Replace, Text = "a", Replacement = "x", Occurrence = occurrence };
        Assert.AreEqual(expected, rule.Apply("a-a-a.txt", EntryKind.File, 0));
    }

    [TestMethod]
    public void Replacement_CaseScopeAndFolderNames()
    {
        var rule = new RenameRule { Method = RenameMethod.Replace, Text = "A", Replacement = "x" };
        Assert.AreEqual("xx.A", rule.Apply("aA.A", EntryKind.File, 0));
        Assert.AreEqual("ax.A", (rule with { MatchCase = true }).Apply("aA.A", EntryKind.File, 0));
        Assert.AreEqual("xx.x", (rule with { Scope = NameScope.FullName }).Apply("aA.A", EntryKind.File, 0));
        Assert.AreEqual("xx.x", rule.Apply("aA.A", EntryKind.Folder, 0));
    }

    [TestMethod]
    public void Positions_CountWholeUnicodeCharacters()
    {
        var insert = new RenameRule { Method = RenameMethod.Insert, Text = "X", Position = 1 };
        Assert.AreEqual("😀XéB.txt", insert.Apply("😀éB.txt", EntryKind.File, 0));
        Assert.AreEqual("😀éXB.txt", (insert with { FromRight = true }).Apply("😀éB.txt", EntryKind.File, 0));
        var delete = new RenameRule { Method = RenameMethod.DeleteRange, Position = 1, Length = 1, FromRight = true };
        Assert.AreEqual("😀B.txt", delete.Apply("😀éB.txt", EntryKind.File, 0));
        Assert.Throws<ArgumentException>(() => (delete with { Length = 4 }).Apply("abc.txt", EntryKind.File, 0));
    }

    [TestMethod]
    public void RemoveCharacterSet_PreservesUnrelatedUnicode()
    {
        var rule = new RenameRule { Method = RenameMethod.RemoveCharacters, Text = "😀a" };
        Assert.AreEqual("😃éb.txt", rule.Apply("😀😃éab.txt", EntryKind.File, 0));
    }

    [TestMethod]
    public void Numbering_IsDeterministic()
    {
        var rule = new RenameRule { Method = RenameMethod.Numbering, Start = 5, Step = 2, Padding = 3, FromRight = true };
        Assert.AreEqual("file_009.pdf", rule.Apply("file.pdf", EntryKind.File, 2));
        Assert.AreEqual("file_009.pdf", rule.Apply("file.pdf", EntryKind.File, 2));
    }

    [TestMethod]
    [DataRow(LetterCase.Lower, "hello world.txt")]
    [DataRow(LetterCase.Upper, "HELLO WORLD.txt")]
    [DataRow(LetterCase.Title, "Hello World.txt")]
    public void CaseConversion_PreservesExtension(LetterCase mode, string expected) =>
        Assert.AreEqual(expected, new RenameRule { Method = RenameMethod.ChangeCase, Case = mode }.Apply("hELLo WORLD.txt", EntryKind.File, 0));

    [TestMethod]
    public void Preset_RoundTripPreservesRepeatedMethods()
    {
        var first = new RenameRule { Method = RenameMethod.Replace, Text = "a", Replacement = "b" };
        var second = new RenameRule { Method = RenameMethod.Replace, Text = "b", Replacement = "c", Occurrence = 2, Enabled = false };
        var preset = RenamePreset.Parse(RenamePreset.Serialize("测试", [first, second]));
        Assert.AreEqual(first, preset.Rules[0]); Assert.AreEqual(second, preset.Rules[1]);
        Assert.AreNotEqual(preset.Rules[0].Id, preset.Rules[1].Id);
        Assert.Throws<ArgumentException>(() => RenamePreset.Parse("{\"Version\":99,\"Name\":\"x\",\"Rules\":[]}"));
        Assert.Throws<ArgumentException>(() => RenamePreset.Serialize("x", [first with { Method = (RenameMethod)99 }]));
    }

    [TestMethod]
    [DataRow("CON.txt")]
    [DataRow("LPT¹.pdf")]
    [DataRow("name.")]
    [DataRow("name ")]
    [DataRow("../escape")]
    [DataRow("a:b")]
    [DataRow("")]
    public void WindowsNames_AreValidated(string name) => Assert.IsNotNull(RenamePlanner.ValidateName(name));

    [TestMethod]
    public void Pipeline_AppliesRepeatedMethodsInOrder()
    {
        using var disk = new TestDirectory(); string source = disk.File("a.old");
        var plan = RenamePlanner.Create([new(source, EntryKind.File)], [
            new() { Method = RenameMethod.Replace, Text = ".old", Replacement = ".new", Scope = NameScope.FullName },
            new() { Method = RenameMethod.Replace, Text = "a", Replacement = "b" },
            new() { Method = RenameMethod.Insert, Text = "_done", FromRight = true },
        ]);
        Assert.AreEqual("b_done.new", plan[0].TargetName); Assert.AreEqual("", plan[0].Error);
    }

    [TestMethod]
    public void Planner_DetectsDiskCollisionDuplicateTargetsAndStepErrors()
    {
        using var disk = new TestDirectory(); string a = disk.File("a.txt"), b = disk.File("b.txt");
        var collision = RenamePlanner.Create([new(a, EntryKind.File)], [new() { Method = RenameMethod.Replace, Text = "a", Replacement = "b" }]);
        Assert.Contains("已存在", collision[0].Error);
        var duplicate = RenamePlanner.Create([new(a, EntryKind.File), new(b, EntryKind.File)], [new() { Method = RenameMethod.Replace, Text = "b", Replacement = "a" }]);
        Assert.IsTrue(duplicate.All(o => o.Error.Length > 0));
        var invalid = RenamePlanner.Create([new(a, EntryKind.File)], [new() { Method = RenameMethod.DeleteRange, Position = 2, Length = 1 }]);
        Assert.Contains("方法 1", invalid[0].Error);
    }

    [TestMethod]
    public void Planner_RejectsParentChildEvenWithInterveningSibling()
    {
        using var disk = new TestDirectory();
        string parent = disk.Folder("a"), sibling = disk.Folder("a!"), child = disk.Folder("a/child");
        var plan = RenamePlanner.Create([new(parent, EntryKind.Folder), new(sibling, EntryKind.Folder), new(child, EntryKind.Folder)], [new() { Method = RenameMethod.Insert, Text = "x" }]);
        Assert.Contains("父文件夹", plan[0].Error); Assert.Contains("父文件夹", plan[2].Error); Assert.AreEqual("", plan[1].Error);
    }

    [TestMethod]
    public void Executor_SwapsFilesWithoutOverwritingContents()
    {
        using var disk = new TestDirectory(); string a = disk.File("a.txt", "A"), b = disk.File("b.txt", "B");
        var result = new RenameExecutor().Execute([new(new(a, EntryKind.File), b), new(new(b, EntryKind.File), a)]);
        Assert.IsTrue(result.Items.All(i => i.Succeeded));
        Assert.AreEqual("B", File.ReadAllText(a)); Assert.AreEqual("A", File.ReadAllText(b));
        Assert.AreEqual(2, Directory.GetFiles(disk.Path).Length);
    }

    [TestMethod]
    public void Executor_RenamesFolderAndChangesOnlyCase()
    {
        using var disk = new TestDirectory(); string source = disk.Folder("Folder.old");
        File.WriteAllText(System.IO.Path.Combine(source, "content.txt"), "keep");
        string target = System.IO.Path.Combine(disk.Path, "folder.old");
        var result = new RenameExecutor().Execute([new(new(source, EntryKind.Folder), target)]);
        Assert.IsTrue(result.Items[0].Succeeded);
        Assert.AreEqual("folder.old", System.IO.Path.GetFileName(Directory.GetDirectories(disk.Path).Single()));
        Assert.AreEqual("keep", File.ReadAllText(System.IO.Path.Combine(target, "content.txt")));
    }

    [TestMethod]
    public void Executor_RechecksExternalTarget()
    {
        using var disk = new TestDirectory(); string source = disk.File("a.txt", "source");
        var plan = RenamePlanner.Create([new(source, EntryKind.File)], [new() { Method = RenameMethod.Insert, Text = "x" }]);
        File.WriteAllText(plan[0].TargetPath, "external");
        var result = new RenameExecutor().Execute(plan);
        Assert.IsFalse(result.Items[0].Succeeded);
        Assert.AreEqual("source", File.ReadAllText(source)); Assert.AreEqual("external", File.ReadAllText(plan[0].TargetPath));
    }

    [TestMethod]
    public void Executor_RestoresAllAfterSecondPhaseFailure()
    {
        using var disk = new TestDirectory(); string a = disk.File("a.txt", "A"), b = disk.File("b.txt", "B");
        var result = new RenameExecutor(new FaultMover { FailAt = 4 }).Execute([new(new(a, EntryKind.File), b), new(new(b, EntryKind.File), a)]);
        Assert.IsTrue(result.Items.All(i => !i.Succeeded));
        Assert.AreEqual("A", File.ReadAllText(a)); Assert.AreEqual("B", File.ReadAllText(b));
        Assert.IsTrue(result.Items.All(i => i.CurrentPath == i.Operation.Source.Path));
    }

    [TestMethod]
    public void Executor_ReportsActualPathWhenRecoveryFails()
    {
        using var disk = new TestDirectory(); string a = disk.File("a.txt", "A");
        var result = new RenameExecutor(new FaultMover { FailFrom = 2 }).Execute([new(new(a, EntryKind.File), System.IO.Path.Combine(disk.Path, "b.txt"))]);
        Assert.IsFalse(result.Items[0].Succeeded); Assert.AreNotEqual(a, result.Items[0].CurrentPath);
        Assert.Contains("当前路径", result.Items[0].Error); Assert.AreEqual("A", File.ReadAllText(result.Items[0].CurrentPath));
    }

    [TestMethod]
    public void Cancellation_AfterStagingRestoresSource()
    {
        using var disk = new TestDirectory(); using var cancellation = new CancellationTokenSource(); string a = disk.File("a.txt");
        var result = new RenameExecutor(new FaultMover { AfterMove = () => cancellation.Cancel() })
            .Execute([new(new(a, EntryKind.File), System.IO.Path.Combine(disk.Path, "b.txt"))], cancellation.Token);
        Assert.IsTrue(result.Cancelled); Assert.IsTrue(File.Exists(a)); Assert.AreEqual(1, Directory.GetFiles(disk.Path).Length);
    }

    private sealed class FaultMover : IRenameFileSystem
    {
        public int FailAt { get; init; } = -1;
        public int FailFrom { get; init; } = int.MaxValue;
        public Action? AfterMove { get; init; }
        private int _moves;
        public void Move(string source, string target, EntryKind kind)
        {
            int current = ++_moves;
            if (current == FailAt || current >= FailFrom) throw new IOException("simulated failure");
            if (kind == EntryKind.File) File.Move(source, target); else Directory.Move(source, target);
            AfterMove?.Invoke();
        }
    }
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BigDvRenameTests", Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public string File(string name, string content = "data")
        { string path = System.IO.Path.Combine(Path, name); System.IO.File.WriteAllText(path, content); return path; }
        public string Folder(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;
        public void Dispose() => Directory.Delete(Path, true);
    }
}
