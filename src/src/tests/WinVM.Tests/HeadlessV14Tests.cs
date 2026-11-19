using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinVM.Controls;
using WinVM.Models;
using WinVM.Services;
using WinVM.ViewModels;
using WinVM.Views;
using Xunit;

namespace WinVM.Tests;

/// <summary>1.4 views rendered headless to e2e-shots/v14-*.png.</summary>
public class HeadlessV14Tests
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

    [AvaloniaFact]
    public async Task Detail_with_perf_charts_tasks_and_toasts()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var prefs = TestPrefs.Isolated();
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero) { SimulateLoad = true, LongOpDuration = TimeSpan.FromSeconds(30) };
        var dlg = new FakeDialogs();
        var vm = new MainViewModel(hv, dlg, prefs, useTimer: false);
        MainViewModel.ToastLifetimeOverride = TimeSpan.Zero;
        try
        {
            var w = new MainWindow(prefs) { DataContext = vm, Width = 1280, Height = 860 };
            w.Show();
            await vm.InitializeAsync();
            for (var i = 0; i < 40; i++) await vm.RefreshCoreAsync(force: true, manual: false);
            var running = vm.Vms.First(v => v.State == VmState.Running);
            vm.SelectedVm = running;
            Pump(10);
            Assert.True(running.Perf.Count >= 40);
            var sparks = w.GetVisualDescendants().OfType<Sparkline>().Where(s => s.IsEffectivelyVisible).ToList();
            Assert.True(sparks.Count >= 2, "sparklines visible: " + sparks.Count);
            Save(w, "v14-01-detail-perf.png");

            // a clone in flight + toasts
            var off = vm.Vms.First(v => v.State == VmState.Off);
            vm.SelectedVm = off;
            dlg.PromptResult = off.Name + " - 副本";
            _ = vm.CloneCommand.ExecuteAsync(null);
            for (var i = 0; i < 200 && (vm.Tasks.Count == 0 || vm.Tasks[0].Progress < 8); i++) { await Task.Delay(20); Pump(1); }
            vm.Notify(ToastKind.Success, "镜像下载完成", "Debian 13.7.0 已通过 SHA-256 校验。", "新建虚拟机", () => { });
            vm.Notify(ToastKind.Warning, "导出已取消", "已清理未完成的文件。");
            Pump(10);
            Assert.Single(vm.Tasks);
            Save(w, "v14-02-clone-in-progress-toasts.png");
            vm.Tasks[0].Cancel();
            for (var i = 0; i < 100 && vm.Tasks.Count > 0; i++) { await Task.Delay(20); Pump(1); }
            vm.Dispose();
            w.Close();
        }
        finally { MainViewModel.ToastLifetimeOverride = null; }
    }

    [AvaloniaFact]
    public void Image_gallery_renders()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var prefs = TestPrefs.Isolated();
        var lib = new ImageLibraryViewModel(prefs);
        var sample = new Dictionary<string, ResolvedIso>
        {
            ["ubuntu-desktop-lts"] = new("ubuntu-26.04.1-desktop-amd64.iso", "https://releases.ubuntu.com/26.04/ubuntu-26.04.1-desktop-amd64.iso", "601e30fbf5d97759367c632e2c33630665039b7e2158fd068403da3ccf1bda1f", 6482409472, "26.04.1"),
            ["ubuntu-server-lts"] = new("ubuntu-26.04.1-live-server-amd64.iso", "https://releases.ubuntu.com/26.04/", "cc8a95cde20f6ced61a322420de00f10cc3c90ced545daa46cb9c1a117f1d927", 2927861760, "26.04.1"),
            ["ubuntu-desktop-2404"] = new("ubuntu-24.04.5.1-desktop-amd64.iso", "https://releases.ubuntu.com/24.04/", "4da4a0c9035da8e68a59a838674f403f0a54472c78a83b4fb7f78d03588f85a7", 6250332160, "24.04.5.1"),
            ["debian-netinst"] = new("debian-13.7.0-amd64-netinst.iso", "https://cdimage.debian.org/", "a7ef94ac2fb9a7fec454552abd629b7cc9d5155c886165a45649f5ce6167e355", 792723456, "13.7.0"),
            ["fedora-workstation"] = new("Fedora-Workstation-Live-44-1.7.x86_64.iso", "https://download.fedoraproject.org/", "1620295f6a00c27c3208f0c00b8ece4eab1ec69b9002152d97488bf26a426ddf", 2851612672, "44"),
            ["linuxmint-cinnamon"] = new("linuxmint-22.3-cinnamon-64bit.iso", "https://mirrors.edge.kernel.org/", "a081ab202cfda17f6924128dbd2de8b63518ac0531bcfe3f1a1b88097c459bd4", 3091660800, "22.3"),
            ["almalinux-minimal"] = new("AlmaLinux-10.2-x86_64-minimal.iso", "https://repo.almalinux.org/", "1b532f534231da0d1cd0ccae622bea6cd588d8a0d7b259f1f131501a6eed41a4", 1651376128, "10.2"),
        };
        foreach (var it in lib.LinuxItems)
            if (sample.TryGetValue(it.Entry.Id, out var r)) it.Resolved = r;
        var deb = lib.LinuxItems.First(i => i.Entry.Id == "debian-netinst");
        deb.IsDownloading = true;
        deb.Progress = 37;
        deb.ProgressText = "280 MB / 756 MB · 18.4 MB/s · 剩余约 26 秒";
        var w = new ImageGalleryWindow { DataContext = lib, Width = 960, Height = 1240 };
        w.Show();
        Pump(10);
        Assert.Contains(lib.WindowsItems, i => i.Entry.IsDownloadable);
        Assert.Contains(lib.MacItems, i => i.Entry.IsUnsupported);
        Save(w, "v14-03-image-gallery.png");
        w.Close();
    }

    [AvaloniaFact]
    public void Preferences_shortcuts_and_host_hint_render()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var prefs = TestPrefs.Isolated();
        prefs.AutoElevate = true;
        var pw = new PreferencesWindow { DataContext = new PreferencesViewModel(prefs, @"C:\Users\Administrator\WinVM\VMs") };
        pw.Show();
        Pump(10);
        var toggles = pw.GetVisualDescendants().OfType<CheckBox>().Concat<Control>(pw.GetVisualDescendants().OfType<ToggleSwitch>()).ToList();
        Assert.Contains(toggles, c => (c as ContentControl)?.Content?.ToString()?.Contains("管理员") == true
                                      || Avalonia.Automation.AutomationProperties.GetName(c)?.Contains("管理员") == true);
        Save(pw, "v14-04-preferences.png");
        pw.Close();

        var sw = new ShortcutsWindow();
        sw.Show();
        Save(sw, "v14-05-shortcuts.png");
        sw.Close();

        var err = new ConfirmWindow("启动失败", "启动失败：主机无法启动虚拟机（固件签名校验失败 0x80090006）。", null, false, "确定", danger: false, showCancel: false, error: true);
        err.SetDetail(HostInfo.HostCannotStartExplanation(27902, "CanaryChannel"));
        err.Show();
        Save(err, "v14-06-error-host-hint.png");
        err.Close();
    }

    [AvaloniaFact]
    public void Create_dialog_with_gallery_image_renders()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero);
        var c = new CreateVmViewModel(hv, TestPrefs.Isolated()) { PickFromGalleryAsync = () => Task.FromResult<ImagePick?>(null) };
        c.ApplyImage(new ImagePick(@"C:\Users\Administrator\WinVM\ISO\ubuntu-26.04.1-desktop-amd64.iso", IsoCatalog.Find("ubuntu-desktop-lts")));
        var w = new CreateVmWindow { DataContext = c };
        w.Show();
        Save(w, "v14-07-new-vm-from-gallery.png");
        w.Close();
    }

    [AvaloniaFact]
    public async Task Dark_detail_perf_renders()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        try
        {
            var prefs = TestPrefs.Isolated();
            var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero) { SimulateLoad = true };
            var vm = new MainViewModel(hv, new FakeDialogs(), prefs, useTimer: false);
            var w = new MainWindow(prefs) { DataContext = vm, Width = 1280, Height = 860 };
            w.Show();
            await vm.InitializeAsync();
            for (var i = 0; i < 30; i++) await vm.RefreshCoreAsync(force: true, manual: false);
            vm.SelectedVm = vm.Vms.First(v => v.State == VmState.Running);
            Save(w, "v14-08-detail-perf-dark.png");
            vm.Dispose();
            w.Close();
        }
        finally { Application.Current!.RequestedThemeVariant = ThemeVariant.Light; }
    }
}
