using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinVM.Models;
using WinVM.Services;

namespace WinVM.ViewModels;

public enum HyperVPhase
{
    Checking,
    Available,
    Unavailable
}

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IHyperVService _hv;
    private readonly IDialogService _dialogs;
    private readonly AppPreferences _prefs;
    private readonly bool _useTimer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private DispatcherTimer? _timer;
    private int _checkpointLoadVersion;
    private bool _syncing;
    private int _refreshFailures;
    private bool _firstLoad = true;

    public ObservableCollection<VmInfo> Vms { get; } = new();
    public ObservableCollection<VmInfo> FilteredVms { get; } = new();
    public ObservableCollection<CheckpointInfo> Checkpoints { get; } = new();
    /// <summary>1.5: parent/child tree of <see cref="Checkpoints"/> with a 「当前位置」 marker.</summary>
    public ObservableCollection<CheckpointNode> CheckpointTree { get; } = new();

    [ObservableProperty] private VmInfo? _selectedVm;
    [ObservableProperty] private CheckpointInfo? _selectedCheckpoint;
    [ObservableProperty] private HyperVPhase _phase = HyperVPhase.Checking;
    [ObservableProperty] private HyperVStatus? _status;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _statusMessage = "就绪";
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private DateTime? _lastRefresh;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _sortMode = "name";
    [ObservableProperty] private bool _isLoadingCheckpoints;
    [ObservableProperty] private string _checkpointError = "";
    [ObservableProperty] private string _theme = "system";
    [ObservableProperty] private bool _autoElevate;

    /// <summary>Raised when the app should exit (e.g. after relaunching elevated).</summary>
    public event Action? ShutdownRequested;
    /// <summary>Raised when the user picks a theme (system | light | dark).</summary>
    public event Action<string>? ThemeRequested;
    public event Action? ExitRequested;

    public MainViewModel(IHyperVService hv, IDialogService dialogs, AppPreferences prefs, bool useTimer = true)
    {
        _hv = hv;
        _dialogs = dialogs;
        _prefs = prefs;
        _useTimer = useTimer;
        _sortMode = prefs.SortMode == "state" ? "state" : "name";
        _theme = prefs.Theme;
        _autoElevate = prefs.AutoElevate;
    }

    public IHyperVService Service => _hv;
    public string VersionText => $"WinVM {HyperVService.AppVersion}";
    public string StorePath => _hv.StoreRoot;

    // ------------------------------------------------------------------ derived view state

    public bool ShowChecking => Phase == HyperVPhase.Checking;
    public bool ShowUnavailable => Phase == HyperVPhase.Unavailable;
    public bool ShowLibrary => Phase == HyperVPhase.Available;
    public bool ShowEmptyLibrary => Phase == HyperVPhase.Available && Vms.Count == 0;
    public bool ShowNoSelection => Phase == HyperVPhase.Available && Vms.Count > 0 && SelectedVm == null;
    public bool ShowDetail => Phase == HyperVPhase.Available && SelectedVm != null;
    public bool HasNoSearchResults => Vms.Count > 0 && FilteredVms.Count == 0 && !string.IsNullOrWhiteSpace(SearchText);
    public int RunningCount => Vms.Count(v => v.State == VmState.Running);
    public string VmCountText => Phase == HyperVPhase.Available ? $"{Vms.Count} 台虚拟机 · {RunningCount} 台运行中" : "—";
    public string LibraryHeader => string.IsNullOrWhiteSpace(SearchText) ? $"虚拟机（{Vms.Count}）" : $"搜索结果（{FilteredVms.Count}/{Vms.Count}）";
    public string LastRefreshText => LastRefresh is { } t ? $"上次刷新 {t:HH:mm:ss}" : "尚未刷新";
    public string HyperVStatusText => Phase switch
    {
        HyperVPhase.Checking => "Hyper-V：正在检测…",
        HyperVPhase.Available => Status?.IsElevated == true ? "Hyper-V：已连接（管理员）" : "Hyper-V：已连接",
        _ => "Hyper-V：不可用"
    };
    public bool IsHyperVOk => Phase == HyperVPhase.Available;
    public bool IsHyperVBad => Phase == HyperVPhase.Unavailable;
    public string HyperVReasonText => Status?.Message ?? "";
    public string HyperVGuidance => HyperVService.GuidanceFor(Status?.Codes);
    /// <summary>The "copy enable command" button only makes sense when Hyper-V itself is missing.</summary>
    public bool ShowEnableCommand => HyperVService.GuidanceFor(Status?.Codes) == HyperVService.EnableGuidance;
    public bool CanRelaunchAsAdmin => Status?.NeedsElevation == true;
    public bool HasCheckpoints => Checkpoints.Count > 0;
    public bool ShowNoCheckpoints => !IsLoadingCheckpoints && Checkpoints.Count == 0 && string.IsNullOrEmpty(CheckpointError);
    public bool IsSortByName => SortMode == "name";
    public bool IsSortByState => SortMode == "state";
    public bool IsThemeSystem => Theme == "system";
    public bool IsThemeLight => Theme == "light";
    public bool IsThemeDark => Theme == "dark";

    private void NotifyLibrary()
    {
        foreach (var n in new[]
                 {
                     nameof(ShowEmptyLibrary), nameof(ShowNoSelection), nameof(ShowDetail), nameof(HasNoSearchResults),
                     nameof(RunningCount), nameof(VmCountText), nameof(LibraryHeader)
                 })
            OnPropertyChanged(n);
    }

    partial void OnPhaseChanged(HyperVPhase value)
    {
        foreach (var n in new[]
                 {
                     nameof(ShowChecking), nameof(ShowUnavailable), nameof(ShowLibrary), nameof(HyperVStatusText),
                     nameof(IsHyperVOk), nameof(IsHyperVBad), nameof(VmCountText)
                 })
            OnPropertyChanged(n);
        NotifyLibrary();
        NotifyCommands();
    }

    partial void OnStatusChanged(HyperVStatus? value)
    {
        OnPropertyChanged(nameof(HyperVReasonText));
        OnPropertyChanged(nameof(HyperVGuidance));
        OnPropertyChanged(nameof(ShowEnableCommand));
        OnPropertyChanged(nameof(CanRelaunchAsAdmin));
        OnPropertyChanged(nameof(HyperVStatusText));
        RelaunchAsAdminCommand.NotifyCanExecuteChanged();
    }

    partial void OnLastRefreshChanged(DateTime? value) => OnPropertyChanged(nameof(LastRefreshText));
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSortModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsSortByName));
        OnPropertyChanged(nameof(IsSortByState));
        _prefs.SortMode = value;
        ApplyFilter();
    }

    partial void OnThemeChanged(string value)
    {
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        _prefs.Theme = value;
    }

    partial void OnAutoElevateChanged(bool value)
    {
        _prefs.AutoElevate = value;
        _prefs.Save();
    }

    partial void OnIsLoadingCheckpointsChanged(bool value) => OnPropertyChanged(nameof(ShowNoCheckpoints));
    partial void OnCheckpointErrorChanged(string value) => OnPropertyChanged(nameof(ShowNoCheckpoints));
    partial void OnSelectedCheckpointChanged(CheckpointInfo? value) => NotifyCommands();

    /// <summary>Selection in the checkpoint tree (the 「当前位置」 marker selects no checkpoint).</summary>
    [ObservableProperty] private CheckpointNode? _selectedCheckpointNode;
    partial void OnSelectedCheckpointNodeChanged(CheckpointNode? value) => SelectedCheckpoint = value?.Checkpoint;

    partial void OnSelectedVmChanged(VmInfo? oldValue, VmInfo? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnSelectedVmPropertyChanged;
        if (newValue != null) newValue.PropertyChanged += OnSelectedVmPropertyChanged;
        NotifyLibrary();
        NotifyCommands();
        if (_syncing) return;
        if (!ReferenceEquals(oldValue, newValue))
            _ = LoadCheckpointsAsync();
    }

    private void OnSelectedVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VmInfo.State) or nameof(VmInfo.CanStart))
            NotifyCommands();
    }

    // ------------------------------------------------------------------ startup / refresh

    public async Task InitializeAsync()
    {
        Phase = HyperVPhase.Checking;
        StatusMessage = "正在检测 Hyper-V…";
        HyperVStatus st;
        try
        {
            st = await _hv.CheckHyperVAsync();
        }
        catch (Exception ex)
        {
            Log.Error("CheckHyperV failed", ex);
            st = new HyperVStatus(false, new[] { "UNKNOWN" }, ErrorMapper.Friendly(ex), false, false);
        }
        Status = st;
        Log.Info($"Hyper-V status: ok={st.Available} codes={string.Join(",", st.Codes)} elevated={st.IsElevated}");
        if (!st.Available)
        {
            Phase = HyperVPhase.Unavailable;
            StatusMessage = "Hyper-V 不可用";
            Vms.Clear();
            ApplyFilter();
            if (AutoElevate && st.NeedsElevation && OperatingSystem.IsWindows())
                await RelaunchAsAdminAsync();
            return;
        }

        Phase = HyperVPhase.Available;
        await RefreshCoreAsync(force: true, manual: false);
        StatusMessage = Vms.Count == 0 ? "资源库为空 · 点击「新建」创建第一台虚拟机" : $"已加载 {Vms.Count} 台虚拟机";
        StartTimer();
    }

    private void StartTimer()
    {
        if (!_useTimer || _timer != null || _disposed) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_prefs.EffectiveRefreshSeconds) };
        _timer.Tick += (_, _) => { if (!_disposed) _ = RefreshCoreAsync(force: false, manual: false); };
        _timer.Start();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Phase != HyperVPhase.Available)
        {
            await InitializeAsync();
            return;
        }
        _hv.RequestFresh(); // pick up changes made outside WinVM (e.g. Hyper-V Manager) right away
        await RefreshCoreAsync(force: true, manual: true);
        await LoadCheckpointsAsync();
    }

    /// <summary>Refreshes the library. Background ticks are skipped while another refresh runs
    /// (no overlapping PowerShell queries); forced refreshes wait their turn.</summary>
    public async Task RefreshCoreAsync(bool force, bool manual)
    {
        if (Phase != HyperVPhase.Available || _disposed) return;
        if (force) await _refreshGate.WaitAsync();
        else if (!_refreshGate.Wait(0)) return;
        try
        {
            IsRefreshing = true;
            var list = await _hv.ListVmsAsync();
            ApplyVmList(list);
            LastRefresh = DateTime.Now;
            _refreshFailures = 0;
            if (manual) StatusMessage = $"已刷新 · {Vms.Count} 台虚拟机";
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _refreshFailures++;
            Log.Warn($"Refresh failed ({_refreshFailures}): {ErrorMapper.Friendly(ex)}");
            if (manual || _refreshFailures == 1 || _refreshFailures % 5 == 0)
                StatusMessage = "刷新失败：" + ErrorMapper.Friendly(ex);
            if (ex is HyperVException { Kind: HvErrorKind.AccessDenied } && _refreshFailures >= 2)
            {
                Status = new HyperVStatus(false, new[] { "ACCESS_DENIED" }, ErrorMapper.Friendly(ex), Elevation.IsElevated, Status?.CanElevate ?? false);
                Phase = HyperVPhase.Unavailable;
            }
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
        }
    }

    /// <summary>Diff-updates the library in place: existing items are updated (no flicker, selection
    /// is kept), new ones appended, vanished ones removed; then the filtered view is re-synced.</summary>
    public void ApplyVmList(IReadOnlyList<VmInfo> fresh)
    {
        var keep = SelectedVm;
        var keepCheckpointCount = keep?.CheckpointCount ?? -1;
        var freshByKey = new Dictionary<string, VmInfo>();
        foreach (var f in fresh) freshByKey.TryAdd(f.Key, f);

        _syncing = true;
        try
        {
            for (var i = Vms.Count - 1; i >= 0; i--)
                if (!freshByKey.ContainsKey(Vms[i].Key))
                    Vms.RemoveAt(i);
            var existing = new Dictionary<string, VmInfo>();
            foreach (var v in Vms) existing.TryAdd(v.Key, v);
            foreach (var f in freshByKey.Values)
            {
                if (existing.TryGetValue(f.Key, out var e)) e.UpdateFrom(f);
                else Vms.Add(f);
            }
            ApplyFilterCore();
        }
        finally
        {
            _syncing = false;
        }

        if (keep != null && FilteredVms.Contains(keep))
        {
            if (!ReferenceEquals(SelectedVm, keep))
            {
                _syncing = true;
                SelectedVm = keep;
                _syncing = false;
            }
            if (keep.CheckpointCount != keepCheckpointCount)
                _ = LoadCheckpointsAsync();
        }
        else if (keep != null)
        {
            SelectedVm = null;
        }
        else if (_firstLoad && FilteredVms.Count > 0)
        {
            SelectedVm = FilteredVms[0];
        }
        _firstLoad = false;
        RecordPerf();
        NotifyLibrary();
        NotifyCommands();
        CheckTrayMenu();
    }

    private void ApplyFilter()
    {
        var keep = SelectedVm;
        _syncing = true;
        try { ApplyFilterCore(); }
        finally { _syncing = false; }
        if (keep != null && FilteredVms.Contains(keep) && !ReferenceEquals(SelectedVm, keep))
        {
            _syncing = true;
            SelectedVm = keep;
            _syncing = false;
        }
        NotifyLibrary();
    }

    public static bool Matches(VmInfo v, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        return v.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || v.StateText.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || v.OsText.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || v.SwitchName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || (v.DvdPath?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private void ApplyFilterCore()
    {
        IEnumerable<VmInfo> src = Vms.Where(v => Matches(v, SearchText));
        src = SortMode == "state"
            ? src.OrderBy(v => v.StateSortOrder).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            : src.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase);
        SyncCollection(FilteredVms, src.ToList());
    }

    /// <summary>Minimal in-place edit of <paramref name="target"/> to equal <paramref name="desired"/>.</summary>
    public static void SyncCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var set = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!set.Contains(target[i]))
                target.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < target.Count && ReferenceEquals(target[i], item)) continue;
            var idx = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], item)) { idx = j; break; }
            if (idx >= 0) target.Move(idx, i);
            else target.Insert(i, item);
        }
        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }

    // ------------------------------------------------------------------ checkpoints

    public async Task LoadCheckpointsAsync()
    {
        var vm = SelectedVm;
        var ver = ++_checkpointLoadVersion;
        if (vm == null || Phase != HyperVPhase.Available)
        {
            Checkpoints.Clear();
            CheckpointTree.Clear();
            OnPropertyChanged(nameof(HasCheckpoints));
            OnPropertyChanged(nameof(ShowNoCheckpoints));
            return;
        }
        IsLoadingCheckpoints = true;
        CheckpointError = "";
        try
        {
            var list = await _hv.ListCheckpointsAsync(vm.Ref);
            if (ver != _checkpointLoadVersion) return;
            var selId = SelectedCheckpoint?.Id;
            Checkpoints.Clear();
            foreach (var c in list) Checkpoints.Add(c);
            CheckpointTree.Clear();
            foreach (var n in CheckpointNode.Build(list)) CheckpointTree.Add(n);
            SelectedCheckpoint = Checkpoints.FirstOrDefault(c => c.Id == selId);
            SelectedCheckpointNode = selId == null ? null : CheckpointNode.Flatten(CheckpointTree).FirstOrDefault(n => n.Checkpoint?.Id == selId);
        }
        catch (Exception ex)
        {
            if (ver == _checkpointLoadVersion) CheckpointError = ErrorMapper.Friendly(ex);
        }
        finally
        {
            if (ver == _checkpointLoadVersion)
            {
                IsLoadingCheckpoints = false;
                OnPropertyChanged(nameof(HasCheckpoints));
                OnPropertyChanged(nameof(ShowNoCheckpoints));
            }
        }
    }

    // ------------------------------------------------------------------ command availability

    private bool Avail => Phase == HyperVPhase.Available && !IsBusy && !SelectedIsLocked;
    private bool CanStart() => Avail && SelectedVm?.CanStart == true;
    private bool CanStop() => Avail && SelectedVm?.CanStop == true;
    private bool CanTurnOff() => Avail && SelectedVm?.CanTurnOff == true;
    private bool CanPause() => Avail && SelectedVm?.CanPause == true;
    private bool CanResume() => Avail && SelectedVm?.CanResume == true;
    private bool CanRestart() => Avail && SelectedVm?.CanRestart == true;
    private bool CanReset() => Avail && SelectedVm?.CanReset == true;
    private bool CanConsole() => Phase == HyperVPhase.Available && SelectedVm?.CanOpenConsole == true;
    private bool CanCheckpoint() => Avail && SelectedVm?.CanCreateCheckpoint == true;
    private bool CanApplyCheckpoint() => Avail && SelectedCheckpoint != null && SelectedVm?.CanApplyCheckpoint == true;
    private bool CanDeleteCheckpoint() => Avail && SelectedCheckpoint != null && SelectedVm != null;
    private bool CanSettings() => Avail && SelectedVm?.CanEditSettings == true;
    private bool CanRename() => Avail && SelectedVm?.CanRename == true;
    private bool CanResize() => Avail && SelectedVm?.CanResizeDisk == true;
    private bool CanDelete() => Avail && SelectedVm?.CanDelete == true;
    private bool CanCreate() => Phase == HyperVPhase.Available && !IsBusy;
    private bool HasSelection() => SelectedVm != null;
    private bool CanRelaunch() => CanRelaunchAsAdmin;

    private void NotifyCommands()
    {
        foreach (var c in new IRelayCommand[]
                 {
                     StartCommand, StopCommand, TurnOffCommand, PauseCommand, ResumeCommand, RestartCommand, ResetCommand,
                     OpenConsoleCommand, OpenRdpCommand, OpenSshCommand, CopyConnectInfoCommand, CreateCheckpointCommand, ApplyCheckpointCommand, DeleteCheckpointCommand,
                     EditSettingsCommand, RenameCommand, ResizeDiskCommand, DeleteCommand, CreateCommand, OpenVmFolderCommand,
                     CloneCommand, ExportCommand, ImportCommand, EditAdvancedSettingsCommand, ShowSwitchManagerCommand, OneClickInstallCommand
                 })
            c.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedIsLocked));
        OnPropertyChanged(nameof(SelectedTask));
        OnPropertyChanged(nameof(HasSelectedTask));
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Runs a VM action with busy state, friendly errors, logging and a follow-up refresh.</summary>
    internal async Task<bool> RunActionAsync(VmInfo vm, string label, Func<Task> action, string? success = null, bool reloadCheckpoints = false)
    {
        IsBusy = true;
        BusyText = $"正在{label}「{vm.Name}」…";
        StatusMessage = BusyText;
        var ok = false;
        try
        {
            await action();
            ok = true;
            StatusMessage = success ?? $"{label}完成：{vm.Name}";
            Log.Info($"{label} OK: {vm.Name}");
        }
        catch (Exception ex)
        {
            var msg = ErrorMapper.Friendly(ex);
            StatusMessage = msg;
            Log.Warn($"{label} FAILED: {vm.Name}: {msg} | {(ex as HyperVException)?.Detail}");
            IsBusy = false;
            if (!_disposed)
            {
                ShowWindowRequested?.Invoke();
                await _dialogs.ShowErrorAsync(label, msg, DetailFor(ex));
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
        await RefreshCoreAsync(force: true, manual: false);
        if (reloadCheckpoints) await LoadCheckpointsAsync();
        return ok;
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateAsync() => CreateWithImageAsync(null);

    public async Task CreateWithImageAsync(ImagePick? image)
    {
        var name = await _dialogs.ShowCreateVmAsync(_hv, _prefs, image);
        if (name == null) return;
        await RefreshCoreAsync(force: true, manual: false);
        var created = Vms.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (created != null)
        {
            if (!FilteredVms.Contains(created)) SearchText = "";
            SelectedVm = created;
        }
        StatusMessage = $"已创建虚拟机「{name}」";
        Notify(ToastKind.Success, "虚拟机已创建", $"「{name}」已就绪，点击「启动」开始安装系统。");
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => SelectedVm is { } vm ? RunActionAsync(vm, "启动", () => _hv.StartAsync(vm.Ref), $"已启动：{vm.Name}") : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (SelectedVm is not { } vm) return;
        if (!await _dialogs.ConfirmAsync("关机", $"向「{vm.Name}」发送关机信号，来宾操作系统将正常关机。\n\n若来宾无响应（未安装集成服务），请改用「强制关闭」。", "关机"))
            return;
        await RunActionAsync(vm, "关机", () => _hv.StopAsync(vm.Ref), $"已关机：{vm.Name}");
    }

    [RelayCommand(CanExecute = nameof(CanTurnOff))]
    private async Task TurnOffAsync()
    {
        if (SelectedVm is not { } vm) return;
        if (!await _dialogs.ConfirmAsync("强制关闭", $"确定强制关闭「{vm.Name}」吗？\n\n相当于直接断电，来宾中未保存的数据会丢失。", "强制关闭", danger: true))
            return;
        await RunActionAsync(vm, "强制关闭", () => _hv.TurnOffAsync(vm.Ref), $"已强制关闭：{vm.Name}");
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private Task PauseAsync() => SelectedVm is { } vm ? RunActionAsync(vm, "暂停", () => _hv.PauseAsync(vm.Ref), $"已暂停：{vm.Name}") : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => SelectedVm is { } vm ? RunActionAsync(vm, "恢复", () => _hv.ResumeAsync(vm.Ref), $"已恢复：{vm.Name}") : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RestartAsync()
    {
        if (SelectedVm is not { } vm) return;
        if (!await _dialogs.ConfirmAsync("重启", $"请求「{vm.Name}」的来宾操作系统正常重启。\n\n需要来宾已安装集成服务；无响应时可使用「重置」。", "重启"))
            return;
        await RunActionAsync(vm, "重启", () => _hv.RestartAsync(vm.Ref), $"已发送重启：{vm.Name}");
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        if (SelectedVm is not { } vm) return;
        if (!await _dialogs.ConfirmAsync("重置", $"确定重置「{vm.Name}」吗？\n\n相当于按下主机的复位键，来宾中未保存的数据会丢失。", "重置", danger: true))
            return;
        await RunActionAsync(vm, "重置", () => _hv.ResetAsync(vm.Ref), $"已重置：{vm.Name}");
    }

    [RelayCommand(CanExecute = nameof(CanConsole))]
    private async Task OpenConsoleAsync()
    {
        if (SelectedVm is not { } vm) return;
        try
        {
            await _hv.OpenConsoleAsync(vm.Ref);
            StatusMessage = $"已打开控制台：{vm.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = ErrorMapper.Friendly(ex);
            await _dialogs.ShowErrorAsync("打开控制台", StatusMessage);
        }
    }

    private bool CanRdp() => Phase == HyperVPhase.Available && SelectedVm?.CanOpenRdp == true;
    private bool CanSsh() => Phase == HyperVPhase.Available && SelectedVm?.CanOpenSsh == true;
    private bool CanCopyConnect() => Phase == HyperVPhase.Available && SelectedVm != null;

    [RelayCommand(CanExecute = nameof(CanRdp))]
    private async Task OpenRdpAsync()
    {
        if (SelectedVm is not { } vm) return;
        try
        {
            await _hv.OpenRdpAsync(vm.PrimaryGuestIp);
            StatusMessage = $"已启动远程桌面：{vm.PrimaryGuestIp}";
        }
        catch (Exception ex)
        {
            StatusMessage = ErrorMapper.Friendly(ex);
            await _dialogs.ShowErrorAsync("远程桌面", StatusMessage);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSsh))]
    private async Task OpenSshAsync()
    {
        if (SelectedVm is not { } vm) return;
        try
        {
            var user = _prefs.LastGuestUser;
            await _hv.OpenSshAsync(vm.PrimaryGuestIp, user);
            StatusMessage = $"已启动 SSH：{vm.PrimaryGuestIp}";
        }
        catch (Exception ex)
        {
            StatusMessage = ErrorMapper.Friendly(ex);
            await _dialogs.ShowErrorAsync("SSH", StatusMessage);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyConnect))]
    private async Task CopyConnectInfoAsync()
    {
        if (SelectedVm is not { } vm) return;
        var info = ConnectInfo.FromVm(vm, _prefs.LastGuestUser);
        await _dialogs.CopyToClipboardAsync(info.ToClipboardText());
        StatusMessage = $"已复制连接信息：{vm.Name}" + (vm.HasGuestIp ? $" ({vm.GuestIpDisplay})" : "");
    }

    [RelayCommand(CanExecute = nameof(CanCheckpoint))]
    private async Task CreateCheckpointAsync()
    {
        if (SelectedVm is not { } vm) return;
        var name = await _dialogs.PromptTextAsync("创建检查点", $"为「{vm.Name}」创建检查点，保存当前状态。",
            $"检查点 {DateTime.Now:yyyy-MM-dd HH:mm}", "创建", "检查点名称", "运行中也可创建；检查点会占用额外磁盘空间，且存在检查点时无法扩容磁盘。");
        if (name == null) return;
        await RunActionAsync(vm, "创建检查点", () => _hv.CreateCheckpointAsync(vm.Ref, name), $"已创建检查点「{name}」", reloadCheckpoints: true);
    }

    [RelayCommand(CanExecute = nameof(CanApplyCheckpoint))]
    private async Task ApplyCheckpointAsync()
    {
        if (SelectedVm is not { } vm || SelectedCheckpoint is not { } cp) return;
        var running = vm.State is VmState.Running or VmState.Paused;
        if (!await _dialogs.ConfirmAsync("应用检查点",
                $"确定将「{vm.Name}」恢复到检查点「{cp.Name}」吗？\n\n当前状态{(running ? "（包括正在运行的会话）" : "")}将丢失。建议先创建一个新检查点。",
                "应用", danger: true))
            return;
        await RunActionAsync(vm, "应用检查点", () => _hv.ApplyCheckpointAsync(vm.Ref, cp.Id), $"已恢复到「{cp.Name}」", reloadCheckpoints: true);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteCheckpoint))]
    private async Task DeleteCheckpointAsync()
    {
        if (SelectedVm is not { } vm || SelectedCheckpoint is not { } cp) return;
        if (!await _dialogs.ConfirmAsync("删除检查点", $"确定删除检查点「{cp.Name}」吗？\n\n删除后无法再恢复到该时刻（虚拟机当前状态不受影响）。", "删除", danger: true))
            return;
        await RunActionAsync(vm, "删除检查点", () => _hv.DeleteCheckpointAsync(vm.Ref, cp.Id), $"已删除检查点「{cp.Name}」", reloadCheckpoints: true);
    }

    [RelayCommand]
    private Task RefreshCheckpointsAsync() => LoadCheckpointsAsync();

    [RelayCommand(CanExecute = nameof(CanSettings))]
    private async Task EditSettingsAsync()
    {
        if (SelectedVm is not { } vm) return;
        if (await _dialogs.ShowSettingsAsync(vm, _hv, _prefs))
        {
            await RefreshCoreAsync(force: true, manual: false);
            StatusMessage = $"设置已保存：{vm.Name}";
        }
    }

    private bool CanAdvanced() => Phase == HyperVPhase.Available && !IsBusy && SelectedVm != null && !SelectedIsLocked;

    [RelayCommand(CanExecute = nameof(CanAdvanced))]
    private async Task EditAdvancedSettingsAsync()
    {
        if (SelectedVm is not { } vm) return;
        if (await _dialogs.ShowAdvancedSettingsAsync(new AdvancedSettingsViewModel(vm, _hv)))
        {
            await RefreshCoreAsync(force: true, manual: false);
            StatusMessage = $"高级设置已保存：{vm.Name}";
            Notify(ToastKind.Success, "高级设置已保存", $"「{vm.Name}」的高级设置已更新。");
        }
    }

    private bool CanSwitchManager() => Phase == HyperVPhase.Available && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSwitchManager))]
    private async Task ShowSwitchManagerAsync()
    {
        var mgr = new SwitchManagerViewModel(_hv, _dialogs, Vms.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        await _dialogs.ShowSwitchManagerAsync(mgr);
        if (mgr.Changed) await RefreshCoreAsync(force: true, manual: false);
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private async Task RenameAsync()
    {
        if (SelectedVm is not { } vm) return;
        var name = await _dialogs.PromptTextAsync("重命名虚拟机", $"为「{vm.Name}」输入新名称：", vm.Name, "重命名", "新名称",
            "名称不能与现有虚拟机重复。磁盘文件与文件夹名称保持不变。");
        if (name == null || name.Trim() == vm.Name) return;
        await RunActionAsync(vm, "重命名", () => _hv.RenameAsync(vm.Ref, name.Trim()), $"已重命名为「{name.Trim()}」");
    }

    [RelayCommand(CanExecute = nameof(CanResize))]
    private async Task ResizeDiskAsync()
    {
        if (SelectedVm is not { } vm) return;
        var cur = vm.DiskSizeBytes / (1024m * 1024m * 1024m);
        var min = Math.Floor(cur) + 1;
        var value = await _dialogs.PromptNumberAsync("扩容虚拟磁盘",
            $"「{vm.Name}」当前磁盘容量 {cur:0.##} GiB。请输入新容量：", Math.Max(min, Math.Ceiling(cur) + 16), min, 4096, "GiB", "扩容",
            "只能增大。存在检查点时需先删除检查点；扩容后还需在来宾系统内扩展分区。");
        if (value is not { } gib) return;
        await RunActionAsync(vm, "扩容磁盘", () => _hv.ResizeVhdAsync(vm.Ref, (int)gib), $"磁盘已扩容至 {(int)gib} GiB（请在来宾系统内扩展分区）");
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (SelectedVm is not { } vm) return;
        var (ok, deleteFiles) = await _dialogs.ConfirmWithOptionAsync("删除虚拟机",
            $"确定删除虚拟机「{vm.Name}」吗？此操作不可撤销。\n\n运行中的虚拟机会先被强制关闭。",
            "同时删除虚拟硬盘文件（含检查点磁盘；WinVM 创建的文件夹也会删除）", false, "删除", danger: true);
        if (!ok) return;
        var removed = await RunActionAsync(vm, "删除", () => _hv.DeleteAsync(vm.Ref, deleteFiles),
            deleteFiles ? $"已删除「{vm.Name}」及其磁盘文件" : $"已删除「{vm.Name}」（磁盘文件已保留）");
        if (removed && ReferenceEquals(SelectedVm, vm)) SelectedVm = FilteredVms.FirstOrDefault();
        if (removed) Notify(ToastKind.Info, "虚拟机已删除", deleteFiles ? $"「{vm.Name}」及其磁盘文件已删除。" : $"「{vm.Name}」已删除，磁盘文件已保留。");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenVmFolder()
    {
        if (SelectedVm is not { } vm) return;
        var dir = !string.IsNullOrEmpty(vm.VhdPath) ? System.IO.Path.GetDirectoryName(vm.VhdPath) : vm.Path;
        if (!string.IsNullOrEmpty(dir)) Elevation.OpenFolder(dir);
    }

    [RelayCommand]
    private void OpenStoreFolder() => Elevation.OpenFolder(_hv.StoreRoot);

    [RelayCommand]
    private void OpenLogFolder() => Elevation.OpenFolder(Log.Directory);

    [RelayCommand]
    private Task ShowAboutAsync() => _dialogs.ShowAboutAsync();

    [RelayCommand]
    private async Task CopyEnableCommandAsync()
    {
        await _dialogs.CopyToClipboardAsync(HyperVService.EnableCommand);
        StatusMessage = "已复制启用 Hyper-V 的命令（需在管理员 PowerShell 中执行并重启）";
    }

    [RelayCommand(CanExecute = nameof(CanRelaunch))]
    private async Task RelaunchAsAdminAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Elevation.RelaunchElevated())
            ShutdownRequested?.Invoke();
        else
        {
            StatusMessage = "已取消以管理员身份启动";
            await Task.CompletedTask;
        }
    }

    [RelayCommand]
    private void SetSort(string? mode) => SortMode = mode == "state" ? "state" : "name";

    [RelayCommand]
    private void SetTheme(string? theme)
    {
        Theme = theme is "light" or "dark" ? theme : "system";
        _prefs.Save();
        ThemeRequested?.Invoke(Theme);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke();

    private volatile bool _disposed;
    public bool IsDisposed => _disposed;

    /// <summary>Fast shutdown (must never block): stops the timer, cancels background work and terminates all
    /// PowerShell processes. In-flight calls unwind with <see cref="OperationCanceledException"/>.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _timer?.Stop();
        _timer = null;
        foreach (var t in Tasks.ToList()) { try { t.Cancel(); } catch { } }
        try { _library?.CancelAll(); } catch { }
        try { (_hv as IDisposable)?.Dispose(); } catch (Exception ex) { Log.Warn("Service dispose: " + ex.Message); }
        Log.Info($"Shutdown: view-model disposed in {sw.ElapsedMilliseconds} ms");
    }
}
