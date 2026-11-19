using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using WinVM.Models;
using WinVM.Services;
using WinVM.ViewModels;

namespace WinVM.Views;

/// <summary>Renders the app's own windows to PNG (Avalonia RenderTargetBitmap). Used by
/// <c>WinVM.exe --screenshot &lt;dir&gt;</c> on Windows and by the headless UI tests on Linux.</summary>
public static class ScreenshotRunner
{
    public static string Capture(Window w, string path)
    {
        var scale = w.RenderScaling <= 0 ? 1 : w.RenderScaling;
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(w.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(w.Bounds.Height * scale)));
        using var rtb = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        rtb.Render(w);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        rtb.Save(path);
        Log.Info("screenshot " + path);
        return path;
    }

    private static async Task Settle(int ms = 600)
    {
        await Task.Delay(ms);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    public static async Task<List<string>> RunAsync(MainWindow main, MainViewModel vm, string dir, string prefix = "")
    {
        var shots = new List<string>();
        Directory.CreateDirectory(dir);
        var t0 = DateTime.Now;
        while (vm.Phase == HyperVPhase.Checking && (DateTime.Now - t0).TotalSeconds < 60) await Task.Delay(200);
        await Settle(1200);

        if (vm.Phase != HyperVPhase.Available)
        {
            shots.Add(Capture(main, Path.Combine(dir, prefix + "01-hyperv-unavailable.png")));
            return shots;
        }

        // 1) main window with the first VM selected
        if (vm.FilteredVms.Count > 0) vm.SelectedVm = vm.FilteredVms[0];
        await Settle(900);
        shots.Add(Capture(main, Path.Combine(dir, prefix + "01-main.png")));

        // 2) a VM with checkpoints (detail pane showing groups + checkpoint list)
        var rich = vm.FilteredVms.OrderByDescending(v => v.CheckpointCount).ThenBy(v => v.StateSortOrder).FirstOrDefault();
        if (rich != null)
        {
            vm.SelectedVm = rich;
            await Settle(1200);
            shots.Add(Capture(main, Path.Combine(dir, prefix + "02-detail.png")));
        }

        // 3) new-VM dialog
        var create = new CreateVmWindow { DataContext = new CreateVmViewModel(vm.Service, AppPreferences.Shared, vm.Vms.Select(v => v.Name)) };
        create.Show(main);
        await Settle(1000);
        shots.Add(Capture(create, Path.Combine(dir, prefix + "03-new-vm-dialog.png")));
        create.Close();

        // 4) settings dialog for an Off VM
        var off = vm.Vms.FirstOrDefault(v => v.State == VmState.Off) ?? vm.Vms.FirstOrDefault();
        if (off != null)
        {
            var settings = new SettingsWindow { DataContext = new SettingsViewModel(off, vm.Service, AppPreferences.Shared) };
            settings.Show(main);
            await Settle(900);
            shots.Add(Capture(settings, Path.Combine(dir, prefix + "04-settings-dialog.png")));
            settings.Close();
        }

        // 5) confirm dialog
        var confirm = new ConfirmWindow("删除虚拟机", "确定删除虚拟机「示例」吗？此操作不可撤销。\n\n运行中的虚拟机会先被强制关闭。",
            "同时删除虚拟硬盘文件（含检查点磁盘；WinVM 创建的文件夹也会删除）", false, "删除", danger: true);
        confirm.Show(main);
        await Settle(700);
        shots.Add(Capture(confirm, Path.Combine(dir, prefix + "05-confirm-dialog.png")));
        confirm.Close();

        // 7) live performance (fills the history from the regular poll)
        var running = vm.Vms.FirstOrDefault(v => v.State == VmState.Running);
        if (running != null)
        {
            vm.SelectedVm = running;
            for (var i = 0; i < 25; i++) await vm.RefreshCoreAsync(force: true, manual: false);
            await Settle(900);
            shots.Add(Capture(main, Path.Combine(dir, prefix + "07-detail-perf.png")));
        }

        // 8) image gallery (resolves the real catalog over the network; bounded)
        try
        {
            var resolve = vm.Library.EnsureResolvedAsync();
            await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(45)));
            var gallery = new ImageGalleryWindow { DataContext = vm.Library };
            gallery.Show(main);
            await Settle(1200);
            shots.Add(Capture(gallery, Path.Combine(dir, prefix + "08-image-gallery.png")));
            gallery.Close();
        }
        catch (Exception ex) { Log.Warn("gallery screenshot: " + ex.Message); }

        // 9) preferences, 10) shortcuts
        var prefsWin = new PreferencesWindow { DataContext = new PreferencesViewModel(AppPreferences.Shared, vm.Service.StoreRoot) };
        prefsWin.Show(main);
        await Settle(800);
        shots.Add(Capture(prefsWin, Path.Combine(dir, prefix + "09-preferences.png")));
        prefsWin.Close();
        var keys = new ShortcutsWindow();
        keys.Show(main);
        await Settle(700);
        shots.Add(Capture(keys, Path.Combine(dir, prefix + "10-shortcuts.png")));
        keys.Close();

        // 11) advanced settings, 12) switch manager, 13) checkpoint tree (1.5). Read-only; never targets agentos-e2e-* VMs.
        var advTarget = vm.Vms.Where(v => !v.Name.StartsWith("agentos-e2e-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.State == VmState.Off ? 0 : 1).FirstOrDefault();
        if (advTarget != null)
        {
            var avm = new AdvancedSettingsViewModel(advTarget, vm.Service, autoLoad: false);
            await avm.LoadAsync();
            var adv = new AdvancedSettingsWindow { DataContext = avm };
            adv.Show(main);
            await Settle(700);
            shots.Add(Capture(adv, Path.Combine(dir, prefix + "11-advanced-security.png")));
            avm.SelectedTab = 2;
            await Settle(500);
            shots.Add(Capture(adv, Path.Combine(dir, prefix + "11b-advanced-boot.png")));
            adv.Close();
        }
        var mgr = new SwitchManagerViewModel(vm.Service, new DialogService(() => main), vm.Vms.ToList(), autoLoad: false);
        await mgr.LoadAsync();
        var swWin = new SwitchManagerWindow { DataContext = mgr };
        swWin.Show(main);
        await Settle(700);
        shots.Add(Capture(swWin, Path.Combine(dir, prefix + "12-switch-manager.png")));
        swWin.Close();
        var withCps = vm.FilteredVms.Where(v => !v.Name.StartsWith("agentos-e2e-", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.CheckpointCount).FirstOrDefault();
        if (withCps is { CheckpointCount: > 0 })
        {
            vm.SelectedVm = withCps;
            await Settle(1200);
            shots.Add(Capture(main, Path.Combine(dir, prefix + "13-checkpoint-tree.png")));
        }

        // 6) dark theme
        if (Application.Current is { } app)
        {
            var old = app.RequestedThemeVariant;
            app.RequestedThemeVariant = ThemeVariant.Dark;
            await Settle(900);
            shots.Add(Capture(main, Path.Combine(dir, prefix + "06-main-dark.png")));
            app.RequestedThemeVariant = old;
            await Settle(300);
        }
        return shots;
    }
}
