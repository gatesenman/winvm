using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using WinVM.Models;
using WinVM.Services;
using WinVM.ViewModels;
using WinVM.Views;

namespace WinVM;

public sealed class AppOptions
{
    public bool Demo { get; set; }
    public bool DemoNoHyperV { get; set; }
    public string? ScreenshotDir { get; set; }
    public string? Theme { get; set; }
    /// <summary>Hidden diagnostics: keep a PowerShell call running for N seconds after start.</summary>
    public int DebugInflightSeconds { get; set; }
}

public partial class App : Application
{
    public static AppOptions Options { get; set; } = new();
    private TrayIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public static void ApplyTheme(string theme)
    {
        if (Current == null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    public static IHyperVService CreateService(AppOptions o)
    {
        if (o.DemoNoHyperV)
            return new DemoHyperVService(forcedStatus: new HyperVStatus(false, new[] { "NOT_ELEVATED", "ACCESS_DENIED" },
                HyperVService.InterpretCodes(new[] { "ACCESS_DENIED", "NOT_ELEVATED" }), false, true));
        if (o.Demo) return new DemoHyperVService();
        return new HyperVService();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var prefs = AppPreferences.Shared;
            ApplyTheme(Options.Theme ?? prefs.Theme);
            var hv = CreateService(Options);
            var window = new MainWindow(prefs);
            _window = window;
            if (Options.Demo || Options.DemoNoHyperV) window.Title += "（演示模式）";
            var vm = new MainViewModel(hv, new DialogService(() => window), prefs);
            _vm = vm;
            vm.ThemeRequested += ApplyTheme;
            vm.ShutdownRequested += () => { window.ExitRequested = true; desktop.Shutdown(); };
            vm.ExitRequested += RequestExit;
            vm.ShowWindowRequested += () => window.ShowFromTray();
            vm.TrayMenuInvalidated += RebuildTrayMenu;
            vm.PreferencesApplied += UpdateTray;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(MainViewModel.IsBusy)) RebuildTrayMenu(); };
            window.DataContext = vm;
            window.TrayAvailable = () => _tray is { IsVisible: true };
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, e) =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { if (_tray != null) { _tray.IsVisible = false; _tray.Dispose(); } } catch { }
                vm.Dispose();
                Log.Info($"Shutdown: lifetime exit handlers done in {sw.ElapsedMilliseconds} ms");
                // Everything that matters is saved and every child process is gone. Leave now instead of waiting for
                // the platform teardown, which was seen to stall ~1.5 s on some hosts (non-elevated session on build 27902).
                if (Options.ScreenshotDir == null)
                {
                    Log.Info($"Shutdown: exit code {e.ApplicationExitCode}");
                    Log.Flush();
                    Environment.Exit(e.ApplicationExitCode);
                }
            };
            window.Closed += (_, _) =>
            {
                Log.Info("Shutdown: main window closed");
                vm.Dispose(); // stop polling and PowerShell right away, before the lifetime finishes
                StartExitWatchdog();
            };
            if (Options.ScreenshotDir == null) UpdateTray();

            window.Opened += async (_, _) =>
            {
                // Paint first, then probe Hyper-V (startup feels instant).
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                var init = vm.InitializeAsync();
                if (Options.ScreenshotDir is { } dir)
                {
                    try
                    {
                        await init;
                        await ScreenshotRunner.RunAsync(window, vm, dir);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("screenshot run failed", ex);
                    }
                    finally
                    {
                        window.ExitRequested = true;
                        desktop.Shutdown();
                    }
                }
                else
                {
                    await init;
                    if (Options.DebugInflightSeconds > 0) _ = vm.StartDebugInflightAsync(Options.DebugInflightSeconds);
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void RequestExit()
    {
        if (_window == null) return;
        _window.ExitRequested = true;
        _window.Close();
    }

    /// <summary>Last line of defence: if anything keeps the process alive after the window closed, exit anyway.</summary>
    private static int _watchdog;
    private static void StartExitWatchdog()
    {
        if (Interlocked.Exchange(ref _watchdog, 1) == 1) return;
        new Thread(() =>
        {
            Thread.Sleep(1500);
            Log.Warn("Shutdown: still running 1.5 s after the window closed – forcing exit");
            Log.Flush();
            Environment.Exit(0);
        }) { IsBackground = true, Name = "exit-watchdog" }.Start();
    }

    // ------------------------------------------------------------------ tray

    private void UpdateTray()
    {
        var want = AppPreferences.Shared.ShowTrayIcon && Options.ScreenshotDir == null;
        if (!want)
        {
            if (_tray != null) { _tray.IsVisible = false; }
            return;
        }
        if (_tray == null)
        {
            try
            {
                _tray = new TrayIcon
                {
                    Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://WinVM/Assets/winvm.ico"))),
                    ToolTipText = "WinVM",
                    Menu = new NativeMenu()
                };
                _tray.Clicked += (_, _) => _window?.ShowFromTray();
                TrayIcon.SetIcons(this, new TrayIcons { _tray });
            }
            catch (Exception ex)
            {
                Log.Warn("Tray icon unavailable: " + ex.Message);
                _tray = null;
                return;
            }
        }
        _tray.IsVisible = true;
        RebuildTrayMenu();
    }

    private void RebuildTrayMenu()
    {
        if (_tray == null || _vm == null || _vm.IsDisposed) return;
        try
        {
            var vm = _vm;
            var menu = new NativeMenu();
            var show = new NativeMenuItem("显示主窗口");
            show.Click += (_, _) => _window?.ShowFromTray();
            menu.Add(show);
            menu.Add(new NativeMenuItemSeparator());

            var vms = vm.TrayVms;
            if (vm.Phase != HyperVPhase.Available)
                menu.Add(new NativeMenuItem(vm.Phase == HyperVPhase.Checking ? "正在检测 Hyper-V…" : "Hyper-V 不可用") { IsEnabled = false });
            else if (vms.Count == 0)
                menu.Add(new NativeMenuItem("（没有虚拟机）") { IsEnabled = false });
            foreach (var v in vms)
            {
                var vmItem = v;
                var sub = new NativeMenu();
                var can = vm.CanTrayAct(v);
                var start = new NativeMenuItem("启动") { IsEnabled = can && v.CanStart };
                start.Click += (_, _) => _ = vm.TrayStartAsync(vmItem);
                var stop = new NativeMenuItem("关机") { IsEnabled = can && v.CanStop };
                stop.Click += (_, _) => _ = vm.TrayStopAsync(vmItem);
                var console = new NativeMenuItem("连接控制台") { IsEnabled = v.CanOpenConsole };
                console.Click += (_, _) => _ = vm.TrayConsoleAsync(vmItem);
                sub.Add(start);
                sub.Add(stop);
                sub.Add(console);
                var mark = v.State switch { VmState.Running => "● ", VmState.Paused => "‖ ", _ => "" };
                menu.Add(new NativeMenuItem($"{mark}{v.Name}（{v.StateText}）") { Menu = sub });
            }
            menu.Add(new NativeMenuItemSeparator());
            var exit = new NativeMenuItem("退出 WinVM");
            exit.Click += (_, _) => RequestExit();
            menu.Add(exit);
            _tray.Menu = menu;
            var running = vm.RunningCount;
            _tray.ToolTipText = vm.Phase == HyperVPhase.Available ? $"WinVM · {running} 台虚拟机运行中" : "WinVM";
        }
        catch (Exception ex)
        {
            Log.Warn("Tray menu rebuild failed: " + ex.Message);
        }
    }
}
