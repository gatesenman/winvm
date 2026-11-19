using WinVM.Models;
using WinVM.Services;
using WinVM.ViewModels;
using Xunit;

namespace WinVM.Tests;

public class V15ViewModelTests
{
    private static async Task<(MainViewModel vm, DemoHyperVService hv, FakeDialogs dlg)> Make()
    {
        var prefs = TestPrefs.Isolated();
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero) { SimulateLoad = false };
        var dlg = new FakeDialogs();
        var vm = new MainViewModel(hv, dlg, prefs, useTimer: false);
        await vm.InitializeAsync();
        return (vm, hv, dlg);
    }

    [Fact]
    public async Task Power_shortcuts_require_active_window_and_selection()
    {
        var (vm, hv, _) = await Make();
        var off = vm.Vms.First(v => v.State == VmState.Off);
        vm.SelectedVm = off;
        await vm.ShortcutPowerCommand.ExecuteAsync("start");
        Assert.Equal("ignored:inactive", vm.LastShortcutResult); // no window registered -> never acts
        vm.IsWindowActive = () => false;
        await vm.ShortcutPowerCommand.ExecuteAsync("start");
        Assert.Equal("ignored:inactive", vm.LastShortcutResult);
        Assert.Equal(VmState.Off, (await hv.ListVmsAsync()).Single(v => v.Id == off.Id).State);
        vm.IsWindowActive = () => true;
        vm.SelectedVm = null;
        await vm.ShortcutPowerCommand.ExecuteAsync("start");
        Assert.Equal("ignored:no-selection", vm.LastShortcutResult);
        vm.SelectedVm = vm.Vms.First(v => v.Id == off.Id);
        await vm.ShortcutPowerCommand.ExecuteAsync("start");
        Assert.StartsWith("start:", vm.LastShortcutResult);
        Assert.Equal(VmState.Running, (await hv.ListVmsAsync()).Single(v => v.Id == off.Id).State);
        vm.Dispose();
    }

    [Fact]
    public async Task Power_shortcut_acts_only_on_selected_vm_and_keeps_confirmations()
    {
        var (vm, hv, dlg) = await Make();
        vm.IsWindowActive = () => true;
        var running = vm.Vms.First(v => v.State == VmState.Running);
        vm.SelectedVm = running;
        var othersBefore = (await hv.ListVmsAsync()).Where(v => v.Id != running.Id).Select(v => (v.Id, v.State)).ToList();
        dlg.ConfirmResult = false;
        await vm.ShortcutPowerCommand.ExecuteAsync("turnoff");
        Assert.Contains("强制关闭", dlg.Confirms);
        Assert.Equal(VmState.Running, (await hv.ListVmsAsync()).Single(v => v.Id == running.Id).State);
        await vm.ShortcutPowerCommand.ExecuteAsync("pause");
        Assert.Equal(VmState.Paused, (await hv.ListVmsAsync()).Single(v => v.Id == running.Id).State);
        vm.SelectedVm = vm.Vms.First(v => v.Id == running.Id);
        await vm.ShortcutPowerCommand.ExecuteAsync("pause");
        Assert.Equal(VmState.Running, (await hv.ListVmsAsync()).Single(v => v.Id == running.Id).State);
        vm.SelectedVm = vm.Vms.First(v => v.Id == running.Id);
        await vm.ShortcutPowerCommand.ExecuteAsync("start");
        Assert.StartsWith("ignored:disabled", vm.LastShortcutResult);
        var othersAfter = (await hv.ListVmsAsync()).Where(v => v.Id != running.Id).Select(v => (v.Id, v.State)).ToList();
        Assert.Equal(othersBefore, othersAfter);
        vm.Dispose();
    }

    [Fact]
    public void Shortcut_list_has_no_ctrl_s_and_documents_power_keys()
    {
        var keys = MainViewModel.Shortcuts.Select(s => s.Keys).ToList();
        Assert.DoesNotContain("Ctrl+S", keys);
        Assert.DoesNotContain("Ctrl+W", keys);
        foreach (var k in new[] { "Ctrl+Shift+S", "Ctrl+Shift+T", "Ctrl+Shift+K", "Ctrl+Shift+P", "Ctrl+Shift+R", "Ctrl+Shift+A" })
            Assert.Contains(k, keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task Advanced_settings_viewmodel_builds_minimal_change_and_saves()
    {
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero) { SimulateLoad = false };
        var win = (await hv.ListVmsAsync()).First(v => v.Name == "Windows 11 开发机");
        var avm = new AdvancedSettingsViewModel(win, hv, autoLoad: false);
        await avm.LoadAsync();
        Assert.True(avm.CanEditFirmware);
        Assert.True(avm.BuildChange().IsEmpty);
        avm.Notes = "开发用";
        avm.DynamicMemory = true;
        avm.MinimumMiB = 2048;
        avm.MaximumMiB = 32768;
        avm.SelectedBoot = avm.BootOrder[1];
        avm.MoveBootUpCommand.Execute(null);
        avm.StopActionIndex = 2;
        var ch = avm.BuildChange();
        Assert.Equal("开发用", ch.Notes);
        Assert.True(ch.DynamicMemory);
        Assert.NotNull(ch.BootOrder);
        Assert.Equal(AutoStopActions.ShutDown, ch.AutoStopAction);
        Assert.Null(ch.Tpm);
        Assert.Null(ch.SecureBoot);
        bool? closed = null;
        avm.CloseRequested += ok => closed = ok;
        await avm.SaveCommand.ExecuteAsync(null);
        Assert.True(closed == true, avm.ErrorText);
        var after = await hv.GetAdvancedSettingsAsync(win.Id);
        Assert.True(after.DynamicMemory);
        Assert.Equal("开发用", after.Notes);
        Assert.Equal(AutoStopActions.ShutDown, after.AutoStopAction);
        Assert.Equal(OsKind.Windows, (await hv.ListVmsAsync()).First(v => v.Id == win.Id).Os);
    }

    [Fact]
    public async Task Advanced_settings_running_vm_allows_only_notes_and_shows_errors()
    {
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero) { SimulateLoad = false };
        var running = (await hv.ListVmsAsync()).First(v => v.State == VmState.Running);
        var avm = new AdvancedSettingsViewModel(running, hv, autoLoad: false);
        await avm.LoadAsync();
        Assert.False(avm.CanEditOffOnly);
        Assert.True(avm.CanEditAlways);
        Assert.Contains("只能修改", avm.StateHint);
        avm.Nested = true;
        await avm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("关机", avm.ErrorText);
        avm.Nested = false;
        avm.Notes = "运行中备注";
        bool? closed = null;
        avm.CloseRequested += ok => closed = ok;
        await avm.SaveCommand.ExecuteAsync(null);
        Assert.True(closed == true, avm.ErrorText);
    }

    [Fact]
    public async Task Switch_manager_create_rename_delete_and_connect()
    {
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero) { SimulateLoad = false };
        var dlg = new FakeDialogs();
        var vms = (await hv.ListVmsAsync()).ToList();
        var mgr = new SwitchManagerViewModel(hv, dlg, vms, autoLoad: false);
        await mgr.LoadAsync();
        Assert.Equal(2, mgr.Switches.Count);
        mgr.SelectedSwitch = mgr.Switches[0];
        Assert.False(mgr.RenameCommand.CanExecute(null));
        Assert.False(mgr.DeleteCommand.CanExecute(null));

        mgr.NewName = "实验室";
        mgr.NewKindIndex = 2;
        await mgr.CreateCommand.ExecuteAsync(null);
        Assert.Contains(mgr.Switches, s => s.Name == "实验室" && s.Kind == SwitchKind.Private);
        Assert.Empty(dlg.Confirms);
        Assert.True(mgr.Changed);

        mgr.NewName = "桥接2";
        mgr.NewKindIndex = 0;
        for (var i = 0; i < 50 && mgr.Adapters.Count == 0; i++) await Task.Delay(10);
        mgr.SelectedAdapter = mgr.Adapters.First(a => !a.BoundToSwitch);
        dlg.ConfirmResult = false;
        await mgr.CreateCommand.ExecuteAsync(null);
        Assert.Contains("创建外部交换机", dlg.Confirms);
        Assert.DoesNotContain(mgr.Switches, s => s.Name == "桥接2");
        Assert.Contains("中断", mgr.CreateHint);

        mgr.SelectedSwitch = mgr.Switches.First(s => s.Name == "外部网络");
        await mgr.DeleteCommand.ExecuteAsync(null);
        Assert.Contains("正被以下虚拟机使用", mgr.ErrorText);

        mgr.SelectedVm = vms.First(v => v.SwitchName == "外部网络");
        mgr.TargetSwitch = "实验室";
        await mgr.ConnectVmCommand.ExecuteAsync(null);
        mgr.SelectedSwitch = mgr.Switches.First(s => s.Name == "外部网络");
        dlg.ConfirmResult = true;
        await mgr.DeleteCommand.ExecuteAsync(null);
        Assert.DoesNotContain(mgr.Switches, s => s.Name == "外部网络");

        mgr.SelectedSwitch = mgr.Switches.First(s => s.Name == "实验室");
        dlg.PromptResult = "实验室-2";
        await mgr.RenameCommand.ExecuteAsync(null);
        Assert.Contains(mgr.Switches, s => s.Name == "实验室-2" && s.InUse);
    }

    [Fact]
    public async Task Main_viewmodel_builds_checkpoint_tree_and_node_selection_maps_to_checkpoint()
    {
        var (vm, _, _) = await Make();
        vm.SelectedVm = vm.Vms.First(v => v.Name == "Windows 11 开发机");
        await vm.LoadCheckpointsAsync();
        Assert.Equal(4, vm.Checkpoints.Count);
        var root = Assert.Single(vm.CheckpointTree, n => !n.IsNowMarker);
        Assert.Equal("全新安装", root.Name);
        Assert.Equal(2, root.Children.Count);
        var vs = root.Children.Single(n => n.Name == "安装 Visual Studio 之后");
        Assert.Contains(vs.Children, n => n.IsNowMarker);
        vm.SelectedCheckpointNode = vs;
        Assert.Equal(vs.Checkpoint, vm.SelectedCheckpoint);
        Assert.True(vm.DeleteCheckpointCommand.CanExecute(null));
        vm.SelectedCheckpointNode = vs.Children.First(n => n.IsNowMarker);
        Assert.Null(vm.SelectedCheckpoint);
        Assert.False(vm.DeleteCheckpointCommand.CanExecute(null));
        vm.Dispose();
    }

    [Fact]
    public async Task Main_viewmodel_opens_advanced_and_switch_manager()
    {
        var (vm, _, dlg) = await Make();
        vm.SelectedVm = vm.Vms.First();
        await vm.EditAdvancedSettingsCommand.ExecuteAsync(null);
        Assert.Equal(1, dlg.AdvancedShown);
        SwitchManagerViewModel? seen = null;
        dlg.OnSwitchManager = m => { seen = m; return Task.CompletedTask; };
        await vm.ShowSwitchManagerCommand.ExecuteAsync(null);
        Assert.Equal(1, dlg.SwitchManagerShown);
        Assert.Equal(vm.Vms.Count, seen!.Vms.Count);
        vm.Dispose();
    }

    [Fact]
    public void Create_vm_windows11_preset_enforces_minimums_and_requires_tpm()
    {
        var prefs = TestPrefs.Isolated();
        prefs.LastCpu = 1; prefs.LastMemoryGiB = 2; prefs.LastDiskGiB = 20;
        var hv = new DemoHyperVService(seed: false, delay: TimeSpan.Zero);
        var c = new CreateVmViewModel(hv, prefs);
        c.PresetIndex = 1;
        Assert.Equal(1, c.OsIndex);
        Assert.True(c.ProcessorCount >= 2 || c.MaxCpu < 2);
        Assert.True(c.MemoryGiB >= 4);
        Assert.True(c.DiskGiB >= 64);
        Assert.Contains("TPM", c.FirmwareText);
        var req = c.BuildRequest();
        Assert.True(req.RequireTpm);
        Assert.Equal(OsKind.Windows, req.Os);
        c.OsIndex = 0;
        Assert.Equal(0, c.PresetIndex);
        Assert.False(c.BuildRequest().RequireTpm);
        c.PresetIndex = 3;
        Assert.Equal(0, c.OsIndex);
        Assert.Equal(40, c.DiskGiB);
    }
}
