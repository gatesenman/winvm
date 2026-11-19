using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinVM.Services;
using WinVM.ViewModels;
using WinVM.Views;
using Xunit;

namespace WinVM.Tests;

/// <summary>Renders real views with Avalonia.Headless + Skia (sample data) to PNG under e2e-shots/.</summary>
public class HeadlessUiTests
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

    private static void Pump(int frames = 6)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static async Task<(MainWindow w, MainViewModel vm, FakeDialogs d)> OpenMain(HyperVStatus? forced = null)
    {
        var prefs = TestPrefs.Isolated();
        var hv = new DemoHyperVService(seed: true, delay: TimeSpan.Zero, forcedStatus: forced);
        var dlg = new FakeDialogs();
        var vm = new MainViewModel(hv, dlg, prefs, useTimer: false);
        var w = new MainWindow(prefs) { DataContext = vm, Width = 1280, Height = 800 };
        w.Show();
        await vm.InitializeAsync();
        Pump(10);
        return (w, vm, dlg);
    }

    private static void Save(Window w, string name)
    {
        Pump(8);
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ShotDir, name));
    }

    [AvaloniaFact]
    public async Task Main_window_renders_library_and_detail()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var (w, vm, _) = await OpenMain();
        Assert.True(vm.ShowDetail);
        var list = w.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "VmList");
        Assert.Equal(5, list.ItemCount);
        Assert.NotNull(w.GetVisualDescendants().OfType<Menu>().FirstOrDefault());
        Save(w, "headless-01-main.png");

        vm.SelectedVm = vm.Vms.Single(v => v.Name == "Windows 11 开发机");
        await vm.LoadCheckpointsAsync();
        Pump(10);
        Save(w, "headless-02-detail-checkpoints.png");

        // toolbar enablement follows state (Off VM: start enabled, shutdown disabled)
        var buttons = w.GetVisualDescendants().OfType<Button>().ToList();
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.Contains(buttons, b => b.Command == vm.StartCommand && b.IsEffectivelyEnabled);
        Assert.Contains(buttons, b => b.Command == vm.StopCommand && !b.IsEffectivelyEnabled);
        w.Close();
    }

    [AvaloniaFact]
    public async Task Dark_theme_renders()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        var (w, vm, _) = await OpenMain();
        vm.SelectedVm = vm.Vms.Single(v => v.Name.StartsWith("Ubuntu"));
        await vm.LoadCheckpointsAsync();
        Save(w, "headless-06-main-dark.png");
        w.Close();
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    }

    [AvaloniaFact]
    public async Task Hyperv_unavailable_page_renders()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var status = new HyperVStatus(false, new[] { "ACCESS_DENIED", "NOT_ELEVATED" },
            HyperVService.InterpretCodes(new[] { "ACCESS_DENIED", "NOT_ELEVATED" }), false, true);
        var (w, vm, _) = await OpenMain(status);
        Assert.True(vm.ShowUnavailable);
        Assert.True(vm.RelaunchAsAdminCommand.CanExecute(null));
        Save(w, "headless-07-hyperv-unavailable.png");
        w.Close();
    }

    [AvaloniaFact]
    public async Task Empty_library_renders()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var prefs = TestPrefs.Isolated();
        var vm = new MainViewModel(new DemoHyperVService(seed: false, delay: TimeSpan.Zero), new FakeDialogs(), prefs, useTimer: false);
        var w = new MainWindow(prefs) { DataContext = vm, Width = 1100, Height = 680 };
        w.Show();
        await vm.InitializeAsync();
        Assert.True(vm.ShowEmptyLibrary);
        Save(w, "headless-08-empty-library.png");
        w.Close();
    }

    [AvaloniaFact]
    public async Task Dialogs_render()
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        var (w, vm, _) = await OpenMain();
        var create = new CreateVmWindow { DataContext = new CreateVmViewModel(vm.Service, TestPrefs.Isolated(), vm.Vms.Select(v => v.Name)) };
        create.Show(w);
        Pump(10);
        Save(create, "headless-03-new-vm-dialog.png");
        create.Close();

        var off = vm.Vms.First(v => v.State == Models.VmState.Off);
        var settings = new SettingsWindow { DataContext = new SettingsViewModel(off, vm.Service, TestPrefs.Isolated()) };
        settings.Show(w);
        Save(settings, "headless-04-settings-dialog.png");
        settings.Close();

        var confirm = new ConfirmWindow("删除虚拟机", "确定删除虚拟机「Fedora 41 Workstation」吗？此操作不可撤销。\n\n运行中的虚拟机会先被强制关闭。",
            "同时删除虚拟硬盘文件（含检查点磁盘；WinVM 创建的文件夹也会删除）", false, "删除", danger: true);
        confirm.Show(w);
        Save(confirm, "headless-05-confirm-dialog.png");
        confirm.Close();

        var prompt = new PromptWindow("扩容虚拟磁盘", "「Windows Server 2022」当前磁盘容量 127 GiB。请输入新容量：", null, "扩容", null,
            "只能增大。存在检查点时需先删除检查点；扩容后还需在来宾系统内扩展分区。", numeric: true, numberValue: 160, numberMin: 128, numberMax: 4096, unit: "GiB");
        prompt.Show(w);
        Save(prompt, "headless-09-resize-dialog.png");
        prompt.Close();

        var about = new AboutWindow();
        about.Show(w);
        Save(about, "headless-10-about.png");
        about.Close();
        w.Close();
    }
}
