using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinVM.Models;
using WinVM.Services;
using WinVM.ViewModels;
using WinVM.Views;
using Xunit;

namespace WinVM.Tests;

/// <summary>1.5 views rendered headless to e2e-shots/v15-*.png, plus real key-gesture tests.</summary>
public class HeadlessV15Tests
{
    private static string ShotDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("WINVM_SHOTS") ?? Path.Combine(Path.GetTempPath(), "winvm-shots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static void Pump(int frames = 8)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Save(Window w, string name)
    {
        Pump(10);
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ShotDir, name));
    }

    private static async Task Settle(Func<bool> until, int ms = 2000)
    {
        for (var i = 0; i < ms / 10 && !until(); i++) { await Task.Delay(10); Pump(1); }
        Pump(4);
    }

    [AvaloniaFact]
    public async Task Ctrl_S_no_longer_starts_but_Ctrl_Shift_S_starts_the_selected_vm()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var prefs = TestPrefs.Isolated();
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero) { SimulateLoad = false };
        var vm = new MainViewModel(hv, new FakeDialogs(), prefs, useTimer: false);
        var w = new MainWindow(prefs) { DataContext = vm, Width = 1180, Height = 760 };
        w.Show();
        w.Activate();
        await vm.InitializeAsync();
        Assert.NotNull(vm.IsWindowActive); // registered by the window
        var off = vm.Vms.First(v => v.State == VmState.Off);
        vm.SelectedVm = off;
        Pump(6);

        w.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
        await Settle(() => false, 300);
        Assert.Equal(VmState.Off, (await hv.ListVmsAsync()).Single(v => v.Id == off.Id).State);
        Assert.Equal("", vm.LastShortcutResult);

        w.KeyPress(Key.S, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.S, "S");
        await Settle(() => vm.LastShortcutResult.Length > 0);
        if (vm.LastShortcutResult == "ignored:inactive")
        {
            // headless platform without activation: the guard did its job; verify the active path explicitly
            vm.IsWindowActive = () => true;
            w.KeyPress(Key.S, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.S, "S");
            await Settle(() => vm.LastShortcutResult.StartsWith("start"));
        }
        Assert.StartsWith("start:", vm.LastShortcutResult);
        await Settle(() => hv.ListVmsAsync().Result.Single(v => v.Id == off.Id).State == VmState.Running);
        Assert.Equal(VmState.Running, (await hv.ListVmsAsync()).Single(v => v.Id == off.Id).State);

        // menus show the documented gestures; nothing is bound to Ctrl+S any more
        var gestures = w.KeyBindings.Select(k => k.Gesture?.ToString()).ToList();
        Assert.DoesNotContain("Ctrl+S", gestures);
        Assert.Contains("Ctrl+Shift+S", gestures);
        Assert.Contains("Ctrl+Shift+T", gestures);
        vm.Dispose();
        w.Close();
    }

    [AvaloniaFact]
    public async Task Advanced_settings_tabs_render()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero) { SimulateLoad = false };
        var win = (await hv.ListVmsAsync()).First(v => v.Name == "Windows 11 开发机");
        await hv.ApplyAdvancedSettingsAsync(win.Id, new AdvancedSettingsChange { DynamicMemory = true, MinimumBytes = 2048L << 20, MaximumBytes = 32768L << 20, Notes = "主力开发机：VS 2026 + WSL\n快照前先关机" });
        var avm = new AdvancedSettingsViewModel(win, hv, autoLoad: false);
        await avm.LoadAsync();
        var w = new AdvancedSettingsWindow { DataContext = avm };
        w.Show();
        var names = new[] { "security", "memory", "boot", "processor", "auto", "notes", "enhanced" };
        for (var i = 0; i < names.Length; i++)
        {
            avm.SelectedTab = i;
            Pump(6);
            Save(w, $"v15-0{i + 1}-advanced-{names[i]}.png");
        }
        var tabs = w.GetVisualDescendants().OfType<TabItem>().Select(t => t.Header?.ToString()).ToList();
        Assert.Equal(new[] { "安全", "内存", "启动顺序", "处理器", "自动操作", "备注", "增强会话" }, tabs);
        w.Close();

        // running VM: firmware controls disabled, hint shown
        var running = (await hv.ListVmsAsync()).First(v => v.State == VmState.Running);
        var rvm = new AdvancedSettingsViewModel(running, hv, autoLoad: false);
        await rvm.LoadAsync();
        var w2 = new AdvancedSettingsWindow { DataContext = rvm };
        w2.Show();
        Save(w2, "v15-08-advanced-running-vm.png");
        Assert.False(rvm.CanEditFirmware);
        w2.Close();
    }

    [AvaloniaFact]
    public async Task Switch_manager_renders()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero) { SimulateLoad = false };
        await hv.CreateSwitchAsync("实验室内网", SwitchKind.Internal);
        await hv.CreateSwitchAsync("隔离测试网", SwitchKind.Private);
        var vms = (await hv.ListVmsAsync()).ToList();
        var mgr = new SwitchManagerViewModel(hv, new FakeDialogs(), vms, autoLoad: false);
        await mgr.LoadAsync();
        mgr.SelectedSwitch = mgr.Switches.First(s => s.Name == "外部网络");
        mgr.NewName = "桥接-WLAN";
        mgr.NewKindIndex = 0;
        await Settle(() => mgr.Adapters.Count > 0);
        mgr.SelectedVm = vms.First(v => v.Name.StartsWith("Ubuntu"));
        mgr.TargetSwitch = "实验室内网";
        var w = new SwitchManagerWindow { DataContext = mgr };
        w.Show();
        Save(w, "v15-09-switch-manager.png");
        Assert.True(mgr.Switches.Count >= 4);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Checkpoint_tree_and_shortcuts_render()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var prefs = TestPrefs.Isolated();
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero) { SimulateLoad = false };
        var vm = new MainViewModel(hv, new FakeDialogs(), prefs, useTimer: false);
        var w = new MainWindow(prefs) { DataContext = vm, Width = 1280, Height = 900 };
        w.Show();
        await vm.InitializeAsync();
        vm.SelectedVm = vm.Vms.First(v => v.Name == "Windows 11 开发机");
        await vm.LoadCheckpointsAsync();
        Pump(10);
        var tree = w.GetVisualDescendants().OfType<TreeView>().First(t => t.Name == "CheckpointTreeView");
        Assert.True(tree.IsEffectivelyVisible);
        var items = tree.GetVisualDescendants().OfType<TreeViewItem>().ToList();
        Assert.True(items.Count >= 5, "tree items: " + items.Count); // 4 checkpoints + 当前位置
        // scroll the checkpoint group into view for the screenshot
        tree.BringIntoView();
        Pump(10);
        Save(w, "v15-10-checkpoint-tree.png");
        vm.Dispose();
        w.Close();

        var sw = new ShortcutsWindow();
        sw.Show();
        Save(sw, "v15-11-shortcuts.png");
        sw.Close();

        var c = new CreateVmViewModel(hv, TestPrefs.Isolated()) { PresetIndex = 1, Name = "Windows 11 专业版" };
        var cw = new CreateVmWindow { DataContext = c };
        cw.Show();
        Save(cw, "v15-12-new-vm-windows11-preset.png");
        cw.Close();
    }
}
