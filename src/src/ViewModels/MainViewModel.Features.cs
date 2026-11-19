using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using WinVM.Models;
using WinVM.Services;

namespace WinVM.ViewModels;

/// <summary>1.4 features: background tasks (clone/export/import/downloads), toasts, live performance,
/// preferences, image gallery and tray support.</summary>
public partial class MainViewModel
{
    public ObservableCollection<BackgroundTaskItem> Tasks { get; } = new();
    public ObservableCollection<ToastItem> Toasts { get; } = new();
    private readonly HashSet<string> _busyVms = new();
    private ImageLibraryViewModel? _library;

    /// <summary>Ask the host window to become visible (e.g. before an error dialog after a tray action).</summary>
    public event Action? ShowWindowRequested;
    /// <summary>The set of VMs or their states changed: rebuild the tray menu.</summary>
    public event Action? TrayMenuInvalidated;
    /// <summary>Preferences were changed in the dialog (tray icon / theme / refresh).</summary>
    public event Action? PreferencesApplied;

    public AppPreferences Preferences => _prefs;

    /// <summary>Shared image gallery (created on first use; downloads survive closing the gallery).</summary>
    public ImageLibraryViewModel Library
    {
        get
        {
            if (_library != null) return _library;
            _library = new ImageLibraryViewModel(_prefs);
            _library.DownloadFinished += OnDownloadFinished;
            _library.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ImageLibraryViewModel.ActiveDownloads)) SyncDownloadTasks();
            };
            return _library;
        }
    }

    internal void SetLibrary(ImageLibraryViewModel lib)
    {
        _library = lib;
        lib.DownloadFinished += OnDownloadFinished;
        lib.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImageLibraryViewModel.ActiveDownloads)) SyncDownloadTasks();
        };
    }

    // ------------------------------------------------------------------ task panel

    public bool HasTasks => Tasks.Count > 0;
    public bool HasLongTasks => Tasks.Count > 0;
    public string TasksSummaryText
    {
        get
        {
            if (Tasks.Count == 0) return "";
            var determinate = Tasks.Where(t => !t.IsIndeterminate).ToList();
            var pct = determinate.Count > 0 ? $" · {determinate.Average(t => t.Progress):0}%" : "";
            return Tasks.Count == 1 ? $"{Tasks[0].Title}{pct}" : $"{Tasks.Count} 个任务进行中{pct}";
        }
    }

    public bool SelectedIsLocked => SelectedVm != null && _busyVms.Contains(SelectedVm.Key);
    public BackgroundTaskItem? SelectedTask => SelectedVm == null ? null : Tasks.FirstOrDefault(t => t.VmKey == SelectedVm.Key);
    public bool HasSelectedTask => SelectedTask != null;

    private void NotifyTasks()
    {
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasLongTasks));
        OnPropertyChanged(nameof(TasksSummaryText));
        NotifyCommands();
    }

    private BackgroundTaskItem AddTask(string title, string? vmKey, bool canCancel = true, Action? onCancel = null)
    {
        var t = new BackgroundTaskItem { Title = title, VmKey = vmKey, CanCancel = canCancel, OnCancel = onCancel };
        t.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BackgroundTaskItem.Progress) or nameof(BackgroundTaskItem.IsIndeterminate))
                OnPropertyChanged(nameof(TasksSummaryText));
        };
        Tasks.Add(t);
        if (vmKey != null) _busyVms.Add(vmKey);
        NotifyTasks();
        return t;
    }

    private void RemoveTask(BackgroundTaskItem t)
    {
        Tasks.Remove(t);
        if (t.VmKey != null && Tasks.All(x => x.VmKey != t.VmKey)) _busyVms.Remove(t.VmKey);
        NotifyTasks();
    }

    /// <summary>Cancels all background work and waits (bounded) for it to clean up. Used before exiting.</summary>
    public async Task CancelAllTasksAsync(TimeSpan wait)
    {
        foreach (var t in Tasks.ToList()) t.Cancel();
        _library?.CancelAll();
        var until = DateTime.UtcNow + wait;
        while (Tasks.Count > 0 && DateTime.UtcNow < until) await Task.Delay(100);
    }

    /// <summary>Runs a long Hyper-V operation as a background task: progress in the task panel, the VM is
    /// locked, errors become a dialog, completion a toast. Returns the operation result or null.</summary>
    internal async Task<string?> RunBackgroundAsync(string title, VmInfo? vm, Func<IProgress<OpProgress>, CancellationToken, Task<string>> work,
        string verb)
    {
        var task = AddTask(title, vm?.Key);
        StatusMessage = title + "…";
        var progress = new Progress<OpProgress>(p => task.Report(p.Fraction, p.Stage));
        try
        {
            var r = await work(progress, task.Token);
            Log.Info($"{title} OK -> {r}");
            return r;
        }
        catch (OperationCanceledException) when (task.Token.IsCancellationRequested || _disposed)
        {
            StatusMessage = $"{verb}已取消";
            Log.Info($"{title} cancelled");
            if (!_disposed) Notify(ToastKind.Warning, $"{verb}已取消", "已清理未完成的文件。");
            return null;
        }
        catch (Exception ex)
        {
            var msg = ErrorMapper.Friendly(ex);
            StatusMessage = msg;
            Log.Warn($"{title} FAILED: {msg} | {(ex as HyperVException)?.Detail}");
            if (!_disposed)
            {
                Notify(ToastKind.Error, $"{verb}失败", msg);
                ShowWindowRequested?.Invoke();
                await _dialogs.ShowErrorAsync(verb, msg, DetailFor(ex));
            }
            return null;
        }
        finally
        {
            RemoveTask(task);
            if (!_disposed) await RefreshCoreAsync(force: true, manual: false);
        }
    }

    public string UniqueVmName(string baseName)
    {
        var names = new HashSet<string>(Vms.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            var n = $"{baseName} {i}";
            if (!names.Contains(n)) return n;
        }
    }

    private bool CanClone() => Avail && SelectedVm?.CanClone == true;
    private bool CanExport() => Avail && SelectedVm?.CanExport == true;
    private bool CanImport() => Phase == HyperVPhase.Available && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanClone))]
    private async Task CloneAsync()
    {
        if (SelectedVm is not { } vm) return;
        var name = await _dialogs.PromptTextAsync("克隆虚拟机",
            $"为「{vm.Name}」创建一份完整副本（新的虚拟机 ID，独立的磁盘文件）。", UniqueVmName(vm.Name + " - 副本"), "克隆", "新虚拟机名称",
            "需要虚拟机处于关机状态，并临时占用约两倍磁盘空间。检查点会一并复制。");
        if (name == null) return;
        name = name.Trim();
        if (name.Length == 0) return;
        if (Vms.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            await _dialogs.ShowErrorAsync("克隆虚拟机", $"已存在同名虚拟机「{name}」，请换一个名称。");
            return;
        }
        var id = await RunBackgroundAsync($"克隆「{vm.Name}」", vm, (p, ct) => _hv.CloneVmAsync(vm.Ref, name, p, ct), "克隆");
        if (id == null) return;
        StatusMessage = $"已克隆为「{name}」";
        SelectByIdOrName(id, name);
        Notify(ToastKind.Success, "克隆完成", $"已创建「{name}」。", "查看", () => SelectByIdOrName(id, name));
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (SelectedVm is not { } vm) return;
        var folder = await _dialogs.PickFolderAsync($"选择「{vm.Name}」的导出位置", _prefs.LastExportFolder);
        if (string.IsNullOrWhiteSpace(folder)) return;
        _prefs.LastExportFolder = folder;
        _prefs.Save();
        var path = await RunBackgroundAsync($"导出「{vm.Name}」", vm, (p, ct) => _hv.ExportVmAsync(vm.Ref, folder, p, ct), "导出");
        if (path == null) return;
        var shown = string.IsNullOrEmpty(path) ? folder : path;
        StatusMessage = $"已导出到 {shown}";
        Notify(ToastKind.Success, "导出完成", $"「{vm.Name}」已导出到 {shown}", "打开文件夹", () => Elevation.OpenFolder(shown));
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        var folder = await _dialogs.PickFolderAsync("选择要导入的虚拟机文件夹（导出生成的文件夹）", _prefs.LastExportFolder);
        if (string.IsNullOrWhiteSpace(folder)) return;
        var leaf = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (leaf.Equals("Virtual Machines", StringComparison.OrdinalIgnoreCase))
        {
            folder = Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? folder;
            leaf = Path.GetFileName(folder);
        }
        var name = await _dialogs.PromptTextAsync("导入虚拟机", $"从「{folder}」导入一份副本到 WinVM 资源库。",
            UniqueVmName(string.IsNullOrWhiteSpace(leaf) ? "导入的虚拟机" : leaf), "导入", "导入后的名称",
            $"文件将复制到 {_hv.StoreRoot}，并生成新的虚拟机 ID；原文件夹保持不变。若原虚拟交换机在本机不存在，网卡将保持未连接。");
        if (name == null) return;
        name = name.Trim();
        if (name.Length == 0) return;
        if (Vms.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            await _dialogs.ShowErrorAsync("导入虚拟机", $"已存在同名虚拟机「{name}」，请换一个名称。");
            return;
        }
        _prefs.LastExportFolder = Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? folder;
        _prefs.Save();
        var id = await RunBackgroundAsync($"导入「{name}」", null, (p, ct) => _hv.ImportVmAsync(folder, name, p, ct), "导入");
        if (id == null) return;
        StatusMessage = $"已导入「{name}」";
        SelectByIdOrName(id, name);
        Notify(ToastKind.Success, "导入完成", $"已添加「{name}」。", "查看", () => SelectByIdOrName(id, name));
    }

    private void SelectByIdOrName(string id, string name)
    {
        var v = Vms.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? Vms.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (v == null) return;
        if (!FilteredVms.Contains(v)) SearchText = "";
        SelectedVm = v;
        ShowWindowRequested?.Invoke();
    }

    // ------------------------------------------------------------------ downloads → tasks / toasts

    private readonly Dictionary<IsoItemViewModel, BackgroundTaskItem> _downloadTasks = new();

    private void SyncDownloadTasks()
    {
        if (_library == null) return;
        foreach (var item in _library.AllItems.Where(i => i.IsDownloading && !_downloadTasks.ContainsKey(i)))
        {
            var it = item;
            var t = AddTask($"下载 {it.Name}", null, true, () => it.CancelDownloadCommand.Execute(null));
            _downloadTasks[it] = t;
            it.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(IsoItemViewModel.Progress) or nameof(IsoItemViewModel.ProgressText) && _downloadTasks.TryGetValue(it, out var tt))
                    tt.Report(it.ProgressText.StartsWith("正在连接", StringComparison.Ordinal) ? -1 : it.Progress / 100.0, it.ProgressText);
            };
        }
    }

    private void OnDownloadFinished(IsoItemViewModel item, Exception? ex)
    {
        if (_downloadTasks.Remove(item, out var t)) RemoveTask(t);
        if (_disposed) return;
        if (ex == null && item.LocalPath is { } path)
        {
            StatusMessage = $"镜像已下载：{Path.GetFileName(path)}";
            Notify(ToastKind.Success, "镜像下载完成", $"{item.Name} {item.VersionText} 已通过 SHA-256 校验。", "新建虚拟机",
                () => _ = CreateWithImageAsync(new ImagePick(path, item.Entry)));
        }
        else if (ex is OperationCanceledException)
            Notify(ToastKind.Info, "下载已暂停", $"{item.Name}：已保留已下载部分，可在镜像库中继续。");
        else if (ex != null)
            Notify(ToastKind.Error, "镜像下载失败", item.ErrorText);
    }

    [RelayCommand]
    private async Task ShowImageGalleryAsync()
    {
        var pick = await _dialogs.ShowImageGalleryAsync(Library, pickMode: false);
        if (Library.ConsumeOneClickRequest() is { } entry)
        {
            await ShowOneClickInstallAsync(entry);
            return;
        }
        if (pick != null && CanCreate()) await CreateWithImageAsync(pick);
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task OneClickInstallAsync() => ShowOneClickInstallAsync(null);

    public async Task ShowOneClickInstallAsync(IsoEntry? initial)
    {
        if (!CanCreate()) return;
        var name = await _dialogs.ShowOneClickInstallAsync(_hv, _prefs, Library, initial);
        if (name == null) return;
        await RefreshCoreAsync(force: true, manual: false);
        var created = Vms.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (created != null)
        {
            if (!FilteredVms.Contains(created)) SearchText = "";
            SelectedVm = created;
        }
        StatusMessage = $"已一键安装「{name}」";
        Notify(ToastKind.Success, "一键安装完成", $"「{name}」已就绪。");
    }

    // ------------------------------------------------------------------ toasts

    public void Notify(ToastKind kind, string title, string message, string? actionText = null, Action? action = null)
    {
        if (_disposed) return;
        if (!_prefs.ShowNotifications && kind is ToastKind.Success or ToastKind.Info) return;
        var t = new ToastItem { Kind = kind, Title = title, Message = message, ActionText = actionText, Action = action };
        t.Dismiss = x => Toasts.Remove(x);
        Toasts.Add(t);
        while (Toasts.Count > 4) Toasts.RemoveAt(0);
        var life = kind == ToastKind.Error ? TimeSpan.FromSeconds(10) : actionText != null ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(5);
        if (ToastLifetimeOverride is { } o) life = o;
        if (life > TimeSpan.Zero && Dispatcher.UIThread.CheckAccess())
            DispatcherTimer.RunOnce(() => Toasts.Remove(t), life);
    }

    /// <summary>Tests/screenshots: keep toasts (zero = never expire).</summary>
    public static TimeSpan? ToastLifetimeOverride { get; set; }

    // ------------------------------------------------------------------ live performance

    public string PerfWindowText => $"最近 {PerfHistory.Capacity * _prefs.EffectiveRefreshSeconds / 60.0:0.#} 分钟";

    /// <summary>Appends one sample per VM from the data just polled (no extra PowerShell calls).</summary>
    private void RecordPerf()
    {
        foreach (var v in Vms)
        {
            var live = v.State is VmState.Running or VmState.Paused;
            if (!live && v.Perf.Count == 0) continue;
            v.Perf.Push(v.State == VmState.Running ? v.CpuUsage : 0, live ? v.MemoryAssignedBytes : 0, v.MemoryStartupBytes);
        }
    }

    // ------------------------------------------------------------------ preferences / help

    [RelayCommand]
    private async Task ShowPreferencesAsync()
    {
        var pvm = new PreferencesViewModel(_prefs, _hv.StoreRoot);
        if (!await _dialogs.ShowPreferencesAsync(pvm)) return;
        ApplyPreferences();
        StatusMessage = "偏好设置已保存";
    }

    /// <summary>Re-reads preferences after the dialog saved them.</summary>
    public void ApplyPreferences()
    {
        if (AutoElevate != _prefs.AutoElevate) AutoElevate = _prefs.AutoElevate;
        if (Theme != _prefs.Theme) { Theme = _prefs.Theme; ThemeRequested?.Invoke(Theme); }
        if (_timer != null) _timer.Interval = TimeSpan.FromSeconds(_prefs.EffectiveRefreshSeconds);
        OnPropertyChanged(nameof(PerfWindowText));
        PreferencesApplied?.Invoke();
    }

    [RelayCommand]
    private Task ShowShortcutsAsync() => _dialogs.ShowShortcutsAsync();

    public static IReadOnlyList<(string Keys, string Action)> Shortcuts { get; } = new[]
    {
        ("Ctrl+N", "新建虚拟机"),
        ("Ctrl+G", "系统镜像库"),
        ("Ctrl+I", "一键安装系统"),
        ("Ctrl+Shift+I", "导入虚拟机"),
        ("Ctrl+E", "导出所选虚拟机"),
        ("Ctrl+D", "克隆所选虚拟机"),
        ("Ctrl+Enter", "连接控制台 / Enhanced Session"),
        ("Ctrl+Shift+S", "启动所选虚拟机"),
        ("Ctrl+Shift+T", "关机（会先确认）"),
        ("Ctrl+Shift+K", "强制关闭（会先确认）"),
        ("Ctrl+Shift+P", "暂停 / 恢复"),
        ("Ctrl+Shift+R", "重启（会先确认）"),
        ("Ctrl+T", "创建检查点"),
        ("Ctrl+Shift+A", "高级设置"),
        ("F2", "重命名"),
        ("Delete", "删除虚拟机（焦点在列表时）"),
        ("F5 / Ctrl+R", "刷新"),
        ("Ctrl+F", "搜索虚拟机"),
        ("Esc", "清除搜索"),
        ("Ctrl+,", "偏好设置"),
        ("F1", "键盘快捷键"),
        ("Ctrl+Q", "退出 WinVM")
    };

    // ------------------------------------------------------------------ power shortcuts (1.5)

    /// <summary>Set by the main window: true only while that WinVM window is the focused (active) window.
    /// Power shortcuts are ignored when it is null or returns false, so a key press can never reach a VM
    /// from another window, a dialog, or while WinVM is in the background.</summary>
    public Func<bool>? IsWindowActive { get; set; }

    /// <summary>Last power shortcut handled ("start", …) or why it was ignored (for tests/logs).</summary>
    public string LastShortcutResult { get; private set; } = "";

    /// <summary>Ctrl+Shift+S/T/K/P/R. Acts only on the focused window's selected VM, only when the matching
    /// toolbar command is enabled, and keeps the same confirmation dialogs as the buttons.</summary>
    [RelayCommand]
    private async Task ShortcutPowerAsync(string? action)
    {
        if (IsWindowActive?.Invoke() != true) { LastShortcutResult = "ignored:inactive"; return; }
        if (SelectedVm is not { } vm) { LastShortcutResult = "ignored:no-selection"; return; }
        IAsyncRelayCommand? cmd = action switch
        {
            "start" => StartCommand,
            "stop" => StopCommand,
            "turnoff" => TurnOffCommand,
            "pause" => vm.State == VmState.Paused ? ResumeCommand : PauseCommand,
            "restart" => RestartCommand,
            _ => null
        };
        if (cmd == null) { LastShortcutResult = "ignored:unknown"; return; }
        if (!cmd.CanExecute(null)) { LastShortcutResult = $"ignored:disabled:{action}"; return; }
        LastShortcutResult = $"{action}:{vm.Name}";
        Log.Info($"shortcut {action} -> {vm.Name} ({vm.Id})");
        await cmd.ExecuteAsync(null);
    }

    // ------------------------------------------------------------------ host problems

    /// <summary>Longer explanation for errors caused by the host rather than the VM.</summary>
    internal static string? DetailFor(Exception ex) => ex is HyperVException { Kind: HvErrorKind.HostCannotStart }
        ? HostInfo.HostCannotStartExplanation(HostInfo.Build, HostInfo.InsiderChannel)
        : null;

    // ------------------------------------------------------------------ tray

    private string _traySignature = "";

    private void CheckTrayMenu()
    {
        var sig = string.Join("|", Vms.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).Select(v => v.Key + ":" + v.Name + ":" + v.State));
        if (sig == _traySignature) return;
        _traySignature = sig;
        TrayMenuInvalidated?.Invoke();
    }

    /// <summary>VMs for the tray menu: running first, then by name (max 12).</summary>
    public IReadOnlyList<VmInfo> TrayVms => Vms.OrderBy(v => v.StateSortOrder).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).Take(12).ToList();

    public bool CanTrayAct(VmInfo vm) => Phase == HyperVPhase.Available && !IsBusy && !_busyVms.Contains(vm.Key);

    public Task TrayStartAsync(VmInfo vm) => CanTrayAct(vm) && vm.CanStart
        ? RunActionAsync(vm, "启动", () => _hv.StartAsync(vm.Ref), $"已启动：{vm.Name}") : Task.CompletedTask;

    public Task TrayStopAsync(VmInfo vm) => CanTrayAct(vm) && vm.CanStop
        ? RunActionAsync(vm, "关机", () => _hv.StopAsync(vm.Ref), $"已关机：{vm.Name}") : Task.CompletedTask;

    public async Task TrayConsoleAsync(VmInfo vm)
    {
        try { await _hv.OpenConsoleAsync(vm.Ref); }
        catch (Exception ex)
        {
            ShowWindowRequested?.Invoke();
            await _dialogs.ShowErrorAsync("打开控制台", ErrorMapper.Friendly(ex));
        }
    }

    /// <summary>Hidden diagnostics (<c>--debug-inflight</c>): an action that keeps the shared PowerShell host busy,
    /// to verify that closing the window mid-operation is still immediate.</summary>
    public async Task StartDebugInflightAsync(int seconds)
    {
        if (_hv is not HyperVService h) return;
        IsBusy = true;
        BusyText = StatusMessage = $"诊断：模拟 {seconds} 秒的操作…";
        Log.Info($"debug-inflight: {seconds}s PowerShell call started");
        try { await h.DebugSleepAsync(seconds); }
        catch (Exception ex) { Log.Info("debug-inflight ended: " + ex.GetType().Name); }
        finally
        {
            if (!_disposed) { IsBusy = false; BusyText = ""; StatusMessage = "诊断操作已结束"; }
        }
    }
}
