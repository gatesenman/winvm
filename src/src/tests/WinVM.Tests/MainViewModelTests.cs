using WinVM.Models;
using WinVM.Services;
using WinVM.ViewModels;
using Xunit;

namespace WinVM.Tests;

public class MainViewModelTests
{
    private static (MainViewModel vm, DemoHyperVService hv, FakeDialogs dlg) Make(HyperVStatus? forced = null)
    {
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero, forcedStatus: forced);
        var dlg = new FakeDialogs();
        var vm = new MainViewModel(hv, dlg, TestPrefs.Isolated(), useTimer: false);
        return (vm, hv, dlg);
    }

    [Fact]
    public async Task Initialize_loads_library_and_selects_first()
    {
        var (vm, _, _) = Make();
        await vm.InitializeAsync();
        Assert.Equal(HyperVPhase.Available, vm.Phase);
        Assert.Equal(5, vm.Vms.Count);
        Assert.Equal(5, vm.FilteredVms.Count);
        Assert.NotNull(vm.SelectedVm);
        Assert.Same(vm.FilteredVms[0], vm.SelectedVm);
        Assert.True(vm.ShowDetail);
        Assert.False(vm.ShowEmptyLibrary);
        Assert.Equal("5 台虚拟机 · 1 台运行中", vm.VmCountText);
        Assert.NotNull(vm.LastRefresh);
    }

    [Fact]
    public async Task Unavailable_status_shows_fix_page_and_disables_commands()
    {
        var (vm, _, _) = Make(new HyperVStatus(false, new[] { "ACCESS_DENIED", "NOT_ELEVATED" }, "没有权限", false, true));
        await vm.InitializeAsync();
        Assert.Equal(HyperVPhase.Unavailable, vm.Phase);
        Assert.True(vm.ShowUnavailable);
        Assert.False(vm.ShowLibrary);
        Assert.True(vm.CanRelaunchAsAdmin);
        Assert.False(vm.CreateCommand.CanExecute(null));
        Assert.Equal("Hyper-V：不可用", vm.HyperVStatusText);
    }

    [Fact]
    public async Task Refresh_diff_updates_in_place_and_keeps_selection()
    {
        var (vm, hv, _) = Make();
        await vm.InitializeAsync();
        var target = vm.Vms.Single(v => v.Name.StartsWith("Windows 11"));
        vm.SelectedVm = target;
        var before = vm.Vms.ToList();

        await hv.StartAsync(target.Id);
        await vm.RefreshCoreAsync(force: true, manual: false);

        Assert.Same(target, vm.SelectedVm);                  // same instance, no flicker
        Assert.Equal(VmState.Running, target.State);         // updated in place
        Assert.True(before.SequenceEqual(vm.Vms));           // no items replaced
    }

    [Fact]
    public void ApplyVmList_adds_and_removes()
    {
        var (vm, _, _) = Make();
        vm.ApplyVmList(new[] { new VmInfo { Name = "A", Id = "1" }, new VmInfo { Name = "B", Id = "2" } });
        var a = vm.Vms[0];
        vm.SelectedVm = vm.Vms[1];
        vm.ApplyVmList(new[] { new VmInfo { Name = "A", Id = "1", State = VmState.Running }, new VmInfo { Name = "C", Id = "3" } });
        Assert.Equal(new[] { "A", "C" }, vm.FilteredVms.Select(v => v.Name));
        Assert.Same(a, vm.Vms.Single(v => v.Id == "1"));
        Assert.Null(vm.SelectedVm); // selected VM disappeared
    }

    [Fact]
    public void Rename_keeps_identity_by_id()
    {
        var (vm, _, _) = Make();
        vm.ApplyVmList(new[] { new VmInfo { Name = "Old", Id = "1" } });
        var item = vm.Vms[0];
        vm.SelectedVm = item;
        vm.ApplyVmList(new[] { new VmInfo { Name = "New", Id = "1" } });
        Assert.Same(item, vm.SelectedVm);
        Assert.Equal("New", item.Name);
    }

    [Fact]
    public async Task Search_and_sort()
    {
        var (vm, _, _) = Make();
        await vm.InitializeAsync();
        vm.SearchText = "windows";
        Assert.Equal(2, vm.FilteredVms.Count);
        Assert.Equal("搜索结果（2/5）", vm.LibraryHeader);
        vm.SearchText = "运行中";
        Assert.Single(vm.FilteredVms);
        vm.SearchText = "zzz";
        Assert.True(vm.HasNoSearchResults);
        vm.SearchText = "";
        vm.SortMode = "state";
        Assert.Equal(VmState.Running, vm.FilteredVms[0].State);
        Assert.Equal(VmState.Off, vm.FilteredVms[^1].State);
        Assert.True(vm.IsSortByState);
    }

    [Fact]
    public void SyncCollection_matches_desired_order()
    {
        var a = new object(); var b = new object(); var c = new object(); var d = new object();
        var col = new System.Collections.ObjectModel.ObservableCollection<object>(new[] { a, b, c });
        MainViewModel.SyncCollection(col, new[] { c, a, d });
        Assert.Equal(new[] { c, a, d }, col);
        MainViewModel.SyncCollection(col, Array.Empty<object>());
        Assert.Empty(col);
    }

    [Fact]
    public async Task Commands_follow_selected_state()
    {
        var (vm, _, _) = Make();
        await vm.InitializeAsync();
        vm.SelectedVm = vm.Vms.Single(v => v.State == VmState.Running);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.True(vm.PauseCommand.CanExecute(null));
        Assert.False(vm.ResumeCommand.CanExecute(null));
        Assert.False(vm.EditSettingsCommand.CanExecute(null));
        Assert.True(vm.OpenConsoleCommand.CanExecute(null));

        vm.SelectedVm = vm.Vms.Single(v => v.State == VmState.Paused);
        Assert.True(vm.ResumeCommand.CanExecute(null));
        Assert.False(vm.PauseCommand.CanExecute(null));

        vm.SelectedVm = null;
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.True(vm.ShowNoSelection);
    }

    [Fact]
    public async Task Start_then_pause_resume_turnoff_round_trip()
    {
        var (vm, _, dlg) = Make();
        await vm.InitializeAsync();
        var w = vm.Vms.Single(v => v.Name == "Windows Server 2022");
        vm.SelectedVm = w;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(VmState.Running, w.State);
        Assert.Contains("已启动", vm.StatusMessage);
        await vm.PauseCommand.ExecuteAsync(null);
        Assert.Equal(VmState.Paused, w.State);
        await vm.ResumeCommand.ExecuteAsync(null);
        Assert.Equal(VmState.Running, w.State);
        await vm.TurnOffCommand.ExecuteAsync(null);
        Assert.Equal(VmState.Off, w.State);
        Assert.Contains("强制关闭", dlg.Confirms);
        Assert.Empty(dlg.Errors);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Cancelled_confirmation_does_nothing()
    {
        var (vm, _, dlg) = Make();
        await vm.InitializeAsync();
        dlg.ConfirmResult = false;
        var before = vm.Vms.Count;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(before, vm.Vms.Count);
    }

    [Fact]
    public async Task Failed_action_shows_error_dialog_with_friendly_text()
    {
        var (vm, _, dlg) = Make();
        await vm.InitializeAsync();
        vm.SelectedVm = vm.Vms.Single(v => v.Name == "Windows 11 开发机");
        dlg.NumberResult = 100; // smaller than current 256 GiB
        await vm.ResizeDiskCommand.ExecuteAsync(null);
        Assert.Single(dlg.Errors);
        Assert.Contains("存在检查点", dlg.Errors[0]);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Delete_removes_and_moves_selection()
    {
        var (vm, hv, dlg) = Make();
        await vm.InitializeAsync();
        var victim = vm.Vms.Single(v => v.Name == "Fedora 41 Workstation");
        vm.SelectedVm = victim;
        dlg.OptionResult = true;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.DoesNotContain(victim, vm.Vms);
        Assert.NotSame(victim, vm.SelectedVm);
        Assert.DoesNotContain(victim.VhdPath, hv.Files);
    }

    [Fact]
    public async Task Checkpoints_load_for_selection_and_crud()
    {
        var (vm, _, dlg) = Make();
        await vm.InitializeAsync();
        vm.SelectedVm = vm.Vms.Single(v => v.Name == "Windows 11 开发机");
        await vm.LoadCheckpointsAsync();
        Assert.Equal(4, vm.Checkpoints.Count);
        Assert.True(vm.HasCheckpoints);

        dlg.PromptResult = "测试检查点";
        await vm.CreateCheckpointCommand.ExecuteAsync(null);
        Assert.Equal(5, vm.Checkpoints.Count);
        Assert.Equal(5, vm.SelectedVm!.CheckpointCount);

        vm.SelectedCheckpoint = vm.Checkpoints.First(c => c.Name == "测试检查点");
        Assert.True(vm.DeleteCheckpointCommand.CanExecute(null));
        await vm.DeleteCheckpointCommand.ExecuteAsync(null);
        Assert.Equal(4, vm.Checkpoints.Count);
        Assert.Null(vm.SelectedCheckpoint);
    }

    [Fact]
    public async Task Create_selects_new_vm()
    {
        var (vm, _, dlg) = Make();
        await vm.InitializeAsync();
        vm.SearchText = "ubuntu";
        dlg.CreateResult = "新的 Debian";
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal("新的 Debian", vm.SelectedVm?.Name);
        Assert.Equal("", vm.SearchText); // cleared so the new VM is visible
    }
}

public class CreateVmViewModelTests
{
    [Fact]
    public async Task Defaults_unique_name_and_iso_detection()
    {
        var hv = new DemoHyperVService(seed: false, delay: TimeSpan.Zero);
        var vm = new CreateVmViewModel(hv, TestPrefs.Isolated(), new[] { "新建虚拟机", "新建虚拟机 2" });
        Assert.Equal("新建虚拟机 3", vm.Name);
        await Task.Delay(50);
        Assert.Contains("Default Switch", vm.Switches);
        Assert.Equal("Default Switch", vm.SelectedSwitch);

        vm.IsoPath = @"D:\ISO\Win11_24H2_x64.iso";
        Assert.Equal(1, vm.OsIndex);
        Assert.Equal("Win11_24H2_x64", vm.Name);
        Assert.Contains("安全启动：开", vm.FirmwareText);

        vm.IsoPath = @"D:\ISO\ubuntu-24.04.iso";
        Assert.Equal(0, vm.OsIndex);
        Assert.Equal("Win11_24H2_x64", vm.Name); // user-visible name not overwritten again
    }

    [Fact]
    public async Task Create_validates_and_closes()
    {
        var hv = new DemoHyperVService(seed: false, delay: TimeSpan.Zero);
        var vm = new CreateVmViewModel(hv, TestPrefs.Isolated());
        bool? closed = null;
        vm.CloseRequested += ok => closed = ok;
        vm.Name = "  ";
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal("请填写虚拟机名称。", vm.ErrorText);
        Assert.Null(closed);

        vm.Name = "Alpine";
        vm.SelectedSwitch = CreateVmViewModel.NoSwitch;
        vm.MemoryGiB = 1.5m;
        var req = vm.BuildRequest();
        Assert.Equal("", req.SwitchName);
        Assert.Equal(1536, req.MemoryMiB);
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Equal("Alpine", vm.CreatedName);
        Assert.True(await hv.VmNameExistsAsync("Alpine"));

        var vm2 = new CreateVmViewModel(hv, TestPrefs.Isolated()) { Name = "alpine" };
        await vm2.CreateCommand.ExecuteAsync(null);
        Assert.Contains("已存在同名虚拟机", vm2.ErrorText);
    }

    [Fact]
    public async Task Settings_only_sends_changed_values()
    {
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero);
        var target = (await hv.ListVmsAsync()).Single(v => v.Name == "Windows Server 2022");
        var vm = new SettingsViewModel(target, hv, TestPrefs.Isolated());
        bool? closed = null;
        vm.CloseRequested += ok => closed = ok;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(closed); // nothing changed -> just close

        vm = new SettingsViewModel(target, hv, TestPrefs.Isolated());
        vm.CloseRequested += ok => closed = ok;
        vm.ProcessorCount = 2;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(closed);
        Assert.Equal(2, (await hv.ListVmsAsync()).Single(v => v.Name == "Windows Server 2022").ProcessorCount);
    }
}

public class SelfTestHarnessTests
{
    [Fact]
    public async Task Demo_selftest_passes_every_step_and_cleans_up()
    {
        TestPrefs.Isolated();
        var demo = new DemoHyperVService(seed: true, delay: TimeSpan.Zero);
        var report = await new SelfTest(demo, p => demo.Files.Contains(p),
            p => demo.Files.Any(f => f.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)), mode: "demo").RunAsync();
        var failed = report.Steps.Where(s => s.Status == "fail").Select(s => s.Name + ": " + s.Detail).ToList();
        Assert.True(failed.Count == 0, string.Join("\n", failed));
        Assert.True(report.CleanupVerified);
        Assert.True(report.Passed >= 48, "passed=" + report.Passed);
        foreach (var n in new[] { "vm.export.cancel", "vm.export", "vm.import", "vm.import.duplicate", "vm.clone", "vm.copies.delete",
                                  "cp.tree", "adv.read", "adv.secureboot", "adv.tpm", "adv.dynmem", "adv.nested", "adv.bootorder", "adv.notes",
                                  "adv.autoactions", "adv.enhanced", "sw.create", "sw.rename", "sw.connect", "sw.delete.blocked",
                                  "sw.default.protected", "sw.delete", "vm.create.win11" })
            Assert.Equal("pass", report.Steps.Single(s => s.Name == n).Status);
        Assert.Equal(5, (await demo.ListVmsAsync()).Count); // pre-existing VMs untouched
        var switches = await demo.ListSwitchesDetailedAsync();
        Assert.Equal(new[] { "Default Switch", "外部网络" }, switches.Select(s => s.Name).ToArray()); // pre-existing switches untouched
    }

    [Fact]
    public async Task Host_that_cannot_start_vms_is_reported_as_environment_issue()
    {
        TestPrefs.Isolated();
        var demo = new DemoHyperVService(seed: true, delay: TimeSpan.Zero)
        {
            StartFailure = new HyperVException(HvErrorKind.HostCannotStart, "启动失败：主机无法启动虚拟机", "Invalid Signature (0x80090006)")
        };
        var report = await new SelfTest(demo, p => demo.Files.Contains(p),
            p => demo.Files.Any(f => f.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)), mode: "demo").RunAsync();
        Assert.True(report.Failed == 0, string.Join("\n", report.Steps.Where(s => s.Status == "fail").Select(s => s.Name + ": " + s.Detail)));
        Assert.Equal("env", report.Steps.Single(s => s.Name == "vm.start").Status);
        Assert.Equal("env", report.Steps.Single(s => s.Name == "vm.pause").Status);
        Assert.Equal("pass", report.Steps.Single(s => s.Name == "vm.delete").Status);
        Assert.Equal(7, report.EnvBlocked);
        Assert.Single(report.EnvironmentIssues);
        Assert.True(report.CleanupVerified);
    }

    [Fact]
    public async Task Selftest_without_hyperv_skips_but_does_not_fail()
    {
        TestPrefs.Isolated();
        var svc = new HyperVService(new FakePsRunner(), isWindows: false);
        var report = await new SelfTest(svc, mode: "nohv").RunAsync();
        Assert.False(report.HyperVAvailable);
        Assert.Equal(0, report.Failed);
        Assert.True(report.Skipped > 15);
        Assert.Contains(report.Steps, s => s.Name == "ui.unavailable-page" && s.Status == "pass");
    }
}
