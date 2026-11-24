namespace BFR.Presentation.Tests;

using BFR.Core;
using BFR.Core.Licensing;
using BFR.Presentation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

/// <summary>
/// Unit tests cho các sửa lỗi độ ổn định (STAB-1/2/9), luồng lõi và tính năng mới v2.2.0
/// trong tầng <c>BFR.Presentation</c>.
/// </summary>
public class PresentationV22Tests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "bfr-v22-" + Guid.NewGuid().ToString("N"));

    public PresentationV22Tests()
    {
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    private (MainViewModel Vm, LicenseService License) CreateViewModel(LicenseTier tier = LicenseTier.Free)
    {
        var fs = new RealFileSystem();
        var license = new LicenseService(new LicenseStore(fs, Path.Combine(_temp, "license.json")), trialEnabled: false);
        if (tier == LicenseTier.Pro) license.RecordPurchase();
        var coordinator = new LicenseCoordinator(license);
        var vm = new MainViewModel(coordinator, null, _temp);
        return (vm, license);
    }

    private static FileItem MakeFile(string name) => new(
        FullPath: Path.Combine(@"C:\data", name),
        Directory: @"C:\data",
        Name: Path.GetFileNameWithoutExtension(name),
        Extension: Path.GetExtension(name).TrimStart('.'),
        IsDirectory: false,
        Size: 128,
        CreatedUtc: DateTime.UtcNow,
        ModifiedUtc: DateTime.UtcNow,
        LoadOrder: 0);

    // ----------------------------------------------------------------- STAB-1: AsyncRelayCommand

    [Fact]
    public async Task AsyncRelayCommand_Execute_IsNonBlockingAndPreventsReentrancy()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int runCount = 0;

        var cmd = new AsyncRelayCommand(async () =>
        {
            Interlocked.Increment(ref runCount);
            await tcs.Task;
        });

        Assert.True(cmd.CanExecute(null));

        // Execute phải trả về ngay lập tức (không GetAwaiter().GetResult() gây deadlock).
        cmd.Execute(null);

        Assert.True(cmd.IsExecuting);
        Assert.False(cmd.CanExecute(null));

        // Gọi Execute lần thứ 2 khi đang chạy phải bị bỏ qua.
        cmd.Execute(null);
        Assert.Equal(1, runCount);

        tcs.SetResult();
        await Task.Delay(50);

        Assert.False(cmd.IsExecuting);
        Assert.True(cmd.CanExecute(null));
    }

    [Fact]
    public async Task AsyncRelayCommand_Execute_InvokesOnErrorAndResetsExecutingState()
    {
        Exception? captured = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var cmd = new AsyncRelayCommand(
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            },
            onError: ex =>
            {
                captured = ex;
                done.TrySetResult();
            });

        cmd.Execute(null);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(20);

        Assert.IsType<InvalidOperationException>(captured);
        Assert.False(cmd.IsExecuting);
        Assert.True(cmd.CanExecute(null));
    }

    // ----------------------------------------------------------------- STAB-2: RebuildAsync thread safety

    [Fact]
    public async Task RebuildAsync_ConcurrentMutations_DoNotThrowAndSettleCleanly()
    {
        var dir = Path.Combine(_temp, "stress");
        Directory.CreateDirectory(dir);
        for (int i = 1; i <= 20; i++)
        {
            File.WriteAllText(Path.Combine(dir, $"file_{i:D2}.txt"), "data");
        }

        var (vm, _) = CreateViewModel(LicenseTier.Pro);
        vm.AddPaths(new[] { dir });
        vm.AddRule("Trim");

        var tasks = new List<Task>();
        for (int i = 0; i < 12; i++)
        {
            if (i % 3 == 0 && vm.Rows.Count > 0) vm.Rows[0].IsSelected = !vm.Rows[0].IsSelected;
            if (i % 4 == 0 && vm.Rows.Count > 1) vm.SetManualOverride(vm.Rows[1], $"custom_{i}.txt");
            tasks.Add(vm.RebuildAsync());
        }

        await Task.WhenAll(tasks);
        await vm.RebuildAsync();

        Assert.Equal(20, vm.TotalCount);
        Assert.False(vm.IsBusy);
    }

    // ----------------------------------------------------------------- Phase 2.1: IsRecursive & IncludeFolders

    [Fact]
    public async Task IsRecursive_Toggle_RaisesPropertyChangedAndRescansSubfolders()
    {
        var root = Path.Combine(_temp, "rec");
        var sub = Path.Combine(root, "nested");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(root, "top.txt"), "a");
        File.WriteAllText(Path.Combine(sub, "deep.txt"), "b");

        var (vm, _) = CreateViewModel();
        vm.AddPaths(new[] { root });
        await vm.RebuildAsync();
        Assert.Equal(1, vm.TotalCount);

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

        vm.IsRecursive = true;
        await Task.Delay(150);
        await vm.RebuildAsync();

        Assert.Contains(nameof(MainViewModel.IsRecursive), raised);
        Assert.Equal(2, vm.TotalCount);
    }

    [Fact]
    public async Task IncludeFolders_Toggle_RaisesPropertyChangedAndIncludesSubfolders()
    {
        var root = Path.Combine(_temp, "inc");
        var sub = Path.Combine(root, "folder_a");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(root, "top.txt"), "a");

        var (vm, _) = CreateViewModel();
        vm.IsRecursive = true;
        vm.AddPaths(new[] { root });
        await vm.RebuildAsync();
        Assert.Equal(1, vm.TotalCount);

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is not null) raised.Add(e.PropertyName); };

        vm.IncludeFolders = true;
        await Task.Delay(150);
        await vm.RebuildAsync();

        Assert.Contains(nameof(MainViewModel.IncludeFolders), raised);
        Assert.Equal(2, vm.TotalCount);
        Assert.Contains(vm.Rows, r => r.File.IsDirectory && r.OldName == "folder_a");
    }

    // ----------------------------------------------------------------- Phase 2.1: RefreshLicenseState

    [Fact]
    public void RefreshLicenseState_UpgradingToPro_ActivatesMetadataProviderImmediately()
    {
        var (vm, license) = CreateViewModel(LicenseTier.Free);
        Assert.False(vm.HasMetadataProvider);

        license.RecordPurchase();
        vm.RefreshLicenseState();

        Assert.True(vm.HasMetadataProvider);
    }

    // ----------------------------------------------------------------- Phase 2.1: RemoveRow re-numbering

    [Fact]
    public async Task RemoveRow_TriggersRebuildAndRenumbersRemainingRows()
    {
        var dir = Path.Combine(_temp, "renum");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "2");
        File.WriteAllText(Path.Combine(dir, "c.txt"), "3");

        var (vm, _) = CreateViewModel();
        vm.LoadPreset(new Preset
        {
            Name = "Num",
            Rules = new List<RuleDefinition>
            {
                new()
                {
                    Type = "Numbering",
                    Enabled = true,
                    Params = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["start"] = System.Text.Json.JsonSerializer.SerializeToElement(1),
                        ["step"] = System.Text.Json.JsonSerializer.SerializeToElement(1),
                        ["width"] = System.Text.Json.JsonSerializer.SerializeToElement(3),
                        ["position"] = System.Text.Json.JsonSerializer.SerializeToElement("ReplaceAll")
                    }
                }
            }
        });
        vm.AddPaths(new[] { dir });
        await vm.RebuildAsync();

        Assert.Equal(new[] { "001.txt", "002.txt", "003.txt" }, vm.Rows.Select(r => r.NewName));

        // Xóa dòng đầu tiên ("a.txt") -> ScheduleRebuild phải chạy và đánh số lại b.txt -> 001.txt, c.txt -> 002.txt.
        vm.RemoveRow(vm.Rows[0]);
        await Task.Delay(350);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(new[] { "001.txt", "002.txt" }, vm.Rows.Select(r => r.NewName));
    }

    // ----------------------------------------------------------------- Feature 1: Manual overrides & CSV round-trip

    [Fact]
    public async Task ManualOverride_ValidatesAndDetectsConflictsAndClearsProperly()
    {
        var dir = Path.Combine(_temp, "manual");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "alpha.txt"), "1");
        File.WriteAllText(Path.Combine(dir, "beta.txt"), "2");

        var (vm, _) = CreateViewModel();
        vm.AddPaths(new[] { dir });
        await vm.RebuildAsync();

        // 1. Valid manual override
        vm.SetManualOverride(vm.Rows[0], "custom_alpha.txt");
        await vm.RebuildAsync();

        Assert.True(vm.HasManualOverrides);
        Assert.Equal(1, vm.ManualOverrideCount);
        Assert.True(vm.Rows[0].HasManualOverride);
        Assert.Equal("custom_alpha.txt", vm.Rows[0].NewName);
        Assert.Equal(PreviewStatus.Ok, vm.Rows[0].Status);

        // 2. Conflict when two rows target the same name
        vm.SetManualOverride(vm.Rows[1], "custom_alpha.txt");
        await vm.RebuildAsync();

        Assert.Equal(2, vm.ConflictCount);
        Assert.Equal(PreviewStatus.Conflict, vm.Rows[0].Status);
        Assert.Equal(PreviewStatus.Conflict, vm.Rows[1].Status);

        // 3. Reserved & invalid characters caught by NameValidator / ConflictDetector (blocking error)
        vm.SetManualOverride(vm.Rows[1], "CON.txt");
        await vm.RebuildAsync();

        Assert.Equal(PreviewStatus.InvalidName, vm.Rows[1].Status);
        Assert.False(vm.CanApply);

        vm.SetManualOverride(vm.Rows[1], "bad:name?.txt");
        await vm.RebuildAsync();

        Assert.True(vm.Rows[1].Status is PreviewStatus.InvalidName or PreviewStatus.Conflict);
        Assert.False(vm.CanApply);

        // 4. Clear single override
        vm.ClearManualOverride(vm.Rows[1]);
        await vm.RebuildAsync();

        Assert.False(vm.Rows[1].HasManualOverride);
        Assert.Equal("beta.txt", vm.Rows[1].NewName);
        Assert.Equal(1, vm.ManualOverrideCount);

        // 5. Clear all overrides
        vm.ClearAllManualOverrides();
        await vm.RebuildAsync();

        Assert.False(vm.HasManualOverrides);
        Assert.False(vm.Rows[0].HasManualOverride);
    }

    [Fact]
    public async Task ExportAndImportCsv_ProtectsAgainstFormulaInjectionAndRoundTrips()
    {
        var dir = Path.Combine(_temp, "csv");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "=calc.txt"), "1");
        File.WriteAllText(Path.Combine(dir, "normal.txt"), "2");

        var (vm, _) = CreateViewModel();
        vm.AddPaths(new[] { dir });
        await vm.RebuildAsync();

        var csv = vm.ExportNamesToCsvText();
        Assert.StartsWith("OldName,NewName", csv);
        // Formula injection protection: '=calc.txt' phải được thêm tiền tố '\''
        Assert.Contains("\"'=calc.txt\"", csv);

        // Import 2-column CSV (với thứ tự đảo ngược)
        var importedCsv = "OldName,NewName\r\n\"normal.txt\",\"renamed_normal.txt\"\r\n\"'=calc.txt\",\"safe_calc.txt\"";
        int applied = vm.ImportNewNamesFromCsvText(importedCsv);
        await vm.RebuildAsync();

        Assert.Equal(2, applied);
        Assert.Equal("safe_calc.txt", vm.Rows.Single(r => r.OldName == "=calc.txt").NewName);
        Assert.Equal("renamed_normal.txt", vm.Rows.Single(r => r.OldName == "normal.txt").NewName);

        // Import plain 1-column lines (positional)
        vm.ClearAllManualOverrides();
        await vm.RebuildAsync();

        int lineApplied = vm.ImportNewNamesFromLines(new[] { "first_line.txt", "second_line.txt" });
        await vm.RebuildAsync();

        Assert.Equal(2, lineApplied);
        Assert.Equal("first_line.txt", vm.Rows[0].NewName);
        Assert.Equal("second_line.txt", vm.Rows[1].NewName);
    }

    // ----------------------------------------------------------------- Phase 3.1: StatusIcon & LCS Diff

    [Theory]
    [InlineData(PreviewStatus.Ok, "\uE73E")]
    [InlineData(PreviewStatus.Unchanged, "\uE738")]
    [InlineData(PreviewStatus.Conflict, "\uE7BA")]
    [InlineData(PreviewStatus.MissingData, "\uE7BA")]
    [InlineData(PreviewStatus.InvalidName, "\uEA39")]
    [InlineData(PreviewStatus.PathTooLong, "\uEA39")]
    [InlineData(PreviewStatus.Protected, "\uEA39")]
    [InlineData(PreviewStatus.Locked, "\uE72E")]
    public void PreviewRow_StatusIcon_MapsEachStatusCorrectly(PreviewStatus status, string expectedGlyph)
    {
        var row = new PreviewRowViewModel(MakeFile("a.txt"), "a.txt", "b.txt", status, null);
        Assert.Equal(expectedGlyph, row.StatusIcon);
    }

    [Fact]
    public void PreviewRow_LcsDiff_HighlightsFineGrainedEditsAndPreservesExactStrings()
    {
        const string oldName = "photo_2024_vacation_01.jpg";
        const string newName = "2024-vacation-01.jpg";

        var (oldSegs, newSegs) = PreviewRowViewModel.Diff(oldName, newName);

        // Ghép các đoạn phải tái tạo chính xác tên gốc và tên mới.
        Assert.Equal(oldName, string.Concat(oldSegs.Select(s => s.Text)));
        Assert.Equal(newName, string.Concat(newSegs.Select(s => s.Text)));

        // LCS phải giữ nguyên "2024" và "vacation" ở trạng thái không highlight.
        Assert.Contains(oldSegs, s => !s.Highlight && s.Text == "2024");
        Assert.Contains(oldSegs, s => !s.Highlight && s.Text == "vacation");
        Assert.Contains(oldSegs, s => s.Highlight && s.Text == "photo_");
        Assert.Contains(newSegs, s => s.Highlight && s.Text == "-");
    }

    [Fact]
    public void PreviewRow_Diff_LongStringsFallbackToPrefixSuffixWithoutTimeout()
    {
        var oldLong = new string('a', 300) + "OLD" + new string('z', 50);
        var newLong = new string('a', 300) + "NEW" + new string('z', 50);

        var (oldSegs, newSegs) = PreviewRowViewModel.Diff(oldLong, newLong);

        Assert.Equal(oldLong, string.Concat(oldSegs.Select(s => s.Text)));
        Assert.Equal(newLong, string.Concat(newSegs.Select(s => s.Text)));
        Assert.Equal(3, oldSegs.Count);
        Assert.Equal("OLD", oldSegs[1].Text);
        Assert.Equal("NEW", newSegs[1].Text);
    }

    [Fact]
    public void DiffSegment_DisplayText_ShowsMiddleDotForHighlightedSpaces()
    {
        var highlighted = new DiffSegment("  ", true);
        var plain = new DiffSegment("  ", false);

        Assert.Equal("··", highlighted.DisplayText);
        Assert.Equal("  ", plain.DisplayText);
    }

    // ----------------------------------------------------------------- Feature 3: Command Palette v2

    [Fact]
    public void CommandPalette_CategoriesAndFuzzyScoring_RanksPrefixAndAcronymAboveSubstring()
    {
        var registry = new CommandRegistry();
        registry.Register("Analyze a folder", "Tool", () => { }, icon: "\uE8B7");
        registry.Register("Add rule: Trim", "Rule", () => { });
        registry.Register("Open Rename Page", "Page", () => { }, shortcut: "Ctrl+2");
        registry.Register("Rename files now", "Action", () => { });
        registry.Register("Apply Preset: Clean Web", "Preset", () => { });

        var vm = new CommandPaletteViewModel(registry, new KeyOnlyStringProvider(), new ImmediateUiDispatcher());

        // Prefix match ("Rename files now") phải xếp trên word/substring match ("Open Rename Page").
        vm.Query = "rename";
        Assert.Equal(2, vm.Filtered.Count);
        Assert.Equal("Rename files now", vm.Filtered[0].Title);
        Assert.Equal("Open Rename Page", vm.Filtered[1].Title);

        // Acronym match ("aaf" -> "Analyze a folder").
        vm.Query = "aaf";
        Assert.NotEmpty(vm.Filtered);
        Assert.Equal("Analyze a folder", vm.Filtered[0].Title);
        Assert.Equal("Tool", vm.Filtered[0].Category);
        Assert.Equal("\uE8B7", vm.Filtered[0].Icon);

        // Category match ("Preset").
        vm.Query = "Preset";
        Assert.Contains(vm.Filtered, c => c.Category == "Preset");
    }

    [Fact]
    public void CommandPalette_ExecuteSpecificCommand_RunsAction()
    {
        var registry = new CommandRegistry();
        int ran = 0;
        registry.Register("First", "Action", () => ran = 1);
        registry.Register("Second", "Action", () => ran = 2);

        var vm = new CommandPaletteViewModel(registry, new KeyOnlyStringProvider(), new ImmediateUiDispatcher());
        var second = vm.Filtered.Single(c => c.Title == "Second");

        Assert.True(vm.Execute(second));
        Assert.Equal(2, ran);
        Assert.Equal(second, vm.Selected);
    }
}
