using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinVM.Models;
using WinVM.Services;

namespace WinVM.ViewModels;

public enum OneClickPhase
{
    Select,
    Resolving,
    Downloading,
    Creating,
    Starting,
    Connecting,
    Done,
    Failed
}

/// <summary>One-shot: pick OS → download official ISO → create VM with recommended preset → optional start + vmconnect.</summary>
public sealed partial class OneClickInstallViewModel : ViewModelBase
{
    private readonly IHyperVService _hv;
    private readonly AppPreferences _prefs;
    private readonly ImageLibraryViewModel _library;
    private readonly HashSet<string> _existing;
    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;

    public ObservableCollection<IsoItemViewModel> LinuxCards { get; }
    public ObservableCollection<IsoItemViewModel> WindowsCards { get; }
    public ObservableCollection<IsoItemViewModel> MacCards { get; }

    [ObservableProperty] private IsoItemViewModel? _selected;
    [ObservableProperty] private string _vmName = "";
    [ObservableProperty] private bool _startAfterCreate = true;
    [ObservableProperty] private bool _openConsoleAfterStart = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private OneClickPhase _phase = OneClickPhase.Select;
    [ObservableProperty] private string _statusText = "选择要安装的操作系统";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private double _progress; // 0..100 for download
    [ObservableProperty] private string? _createdName;
    [ObservableProperty] private string _switchName = "Default Switch";

    // 基础环境 / first-boot (best-effort scripts)
    [ObservableProperty] private bool _enableBasicEnv = true;
    [ObservableProperty] private bool _enableRdp = true;
    [ObservableProperty] private bool _enableOpenSsh = true;
    [ObservableProperty] private bool _agentReadyProfile;
    [ObservableProperty] private string _guestHostname = "";
    [ObservableProperty] private string _guestUsername = "";
    [ObservableProperty] private string _guestPassword = "";
    [ObservableProperty] private string _setupFolderHint = "";

    public event Action<bool>? CloseRequested;
    public string DownloadFolder => _prefs.EffectiveIsoFolder;
    public bool CanRun => Selected is { Entry.IsDownloadable: true } && !IsBusy && !string.IsNullOrWhiteSpace(VmName);
    public bool CanSelect => !IsBusy && Phase is OneClickPhase.Select or OneClickPhase.Failed or OneClickPhase.Done;
    public string PhaseText => Phase switch
    {
        OneClickPhase.Select => "选择系统",
        OneClickPhase.Resolving => "解析下载地址",
        OneClickPhase.Downloading => "下载镜像",
        OneClickPhase.Creating => "创建虚拟机",
        OneClickPhase.Starting => "启动虚拟机",
        OneClickPhase.Connecting => "打开控制台",
        OneClickPhase.Done => "完成",
        OneClickPhase.Failed => "失败",
        _ => ""
    };

    public OneClickInstallViewModel(IHyperVService hv, AppPreferences prefs, ImageLibraryViewModel library,
        IEnumerable<string>? existingNames = null, HttpClient? http = null)
    {
        _hv = hv;
        _prefs = prefs;
        _library = library;
        _http = http ?? IsoCatalog.CreateHttpClient();
        _existing = new HashSet<string>(existingNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        LinuxCards = new ObservableCollection<IsoItemViewModel>(library.LinuxItems);
        WindowsCards = new ObservableCollection<IsoItemViewModel>(library.WindowsItems.Where(i => i.Entry.IsDownloadable || i.IsLinkOnly));
        MacCards = new ObservableCollection<IsoItemViewModel>(library.MacItems);
        GuestUsername = prefs.LastGuestUser ?? "";
        GuestHostname = prefs.LastGuestHostname ?? "";
        AgentReadyProfile = prefs.PreferAgentReadyProfile;
        _ = LoadSwitchAsync();
    }

    /// <summary>Pre-select a gallery entry (e.g. from 「一键安装」 on a card).</summary>
    public void SelectEntry(IsoEntry entry)
    {
        var item = LinuxCards.Concat(WindowsCards).FirstOrDefault(i => i.Entry.Id == entry.Id);
        if (item != null) Selected = item;
    }

    partial void OnSelectedChanged(IsoItemViewModel? value)
    {
        ErrorText = "";
        if (value == null) { StatusText = "选择要安装的操作系统"; return; }
        if (value.Entry.IsUnsupported)
        {
            StatusText = "此系统无法在 Hyper-V 中安装";
            VmName = "";
        }
        else
        {
            StatusText = value.RecommendText;
            VmName = SuggestName(value.Entry, _existing);
        }
        NotifyCan();
    }

    partial void OnVmNameChanged(string value) => NotifyCan();
    partial void OnIsBusyChanged(bool value) => NotifyCan();
    partial void OnPhaseChanged(OneClickPhase value)
    {
        OnPropertyChanged(nameof(PhaseText));
        OnPropertyChanged(nameof(CanSelect));
        NotifyCan();
    }

    private void NotifyCan()
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanSelect));
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public static string SuggestName(IsoEntry e, IEnumerable<string>? existing = null)
    {
        var set = new HashSet<string>(existing ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var baseName = e.Family switch
        {
            "Ubuntu" when e.Id.Contains("server", StringComparison.OrdinalIgnoreCase) => "Ubuntu-服务器",
            "Ubuntu" => "Ubuntu-桌面",
            "Linux Mint" => "LinuxMint-桌面",
            "Debian" => "Debian",
            "Fedora" => "Fedora-桌面",
            "AlmaLinux" => "AlmaLinux",
            "Windows" when e.Id.Contains("11") => "Windows11",
            "Windows" when e.Id.Contains("10") => "Windows10",
            "Windows" => "Windows",
            _ => e.Family
        };
        var n = $"{baseName}-1";
        for (var i = 2; set.Contains(n); i++) n = $"{baseName}-{i}";
        return n;
    }

    private string UniqueName(string baseName)
    {
        var n = baseName.Trim();
        if (string.IsNullOrEmpty(n)) n = "WinVM-VM";
        if (!_existing.Contains(n)) return n;
        for (var i = 2; ; i++)
        {
            var c = $"{n}-{i}";
            if (!_existing.Contains(c)) return c;
        }
    }

    private async Task LoadSwitchAsync()
    {
        try
        {
            var list = (await _hv.ListSwitchesAsync()).ToList();
            if (list.Contains("Default Switch")) SwitchName = "Default Switch";
            else if (list.Count > 0) SwitchName = list[0];
            else SwitchName = "";
        }
        catch { SwitchName = ""; }
    }

    [RelayCommand]
    private void Pick(IsoItemViewModel? item)
    {
        if (!CanSelect || item == null) return;
        Selected = item;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (Selected is not { } item || item.Entry.IsUnsupported) return;
        ErrorText = "";
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            // 1) Resolve
            Phase = OneClickPhase.Resolving;
            StatusText = $"正在解析 {item.Name} 官方下载地址…";
            Progress = 0;
            if (item.Resolved == null)
                await item.ResolveAsync(_http);
            if (item.Resolved == null)
                throw new IsoException(string.IsNullOrEmpty(item.ResolveError)
                    ? "无法解析下载地址。"
                    : item.ResolveError);

            // 2) Download (skip if already verified)
            item.RefreshLocalState();
            string isoPath;
            if (item.LocalPath != null)
            {
                isoPath = item.LocalPath;
                StatusText = "镜像已就绪，跳过下载";
            }
            else
            {
                Phase = OneClickPhase.Downloading;
                StatusText = "正在下载官方镜像…";
                using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var progressPump = Task.Run(async () =>
                {
                    while (!progressCts.Token.IsCancellationRequested)
                    {
                        Progress = item.Progress;
                        if (!string.IsNullOrEmpty(item.StatusText)) StatusText = item.StatusText;
                        try { await Task.Delay(200, progressCts.Token); }
                        catch (OperationCanceledException) { break; }
                    }
                }, progressCts.Token);
                try
                {
                    if (item.IsDownloading)
                    {
                        while (item.IsDownloading)
                        {
                            ct.ThrowIfCancellationRequested();
                            await Task.Delay(200, ct);
                        }
                    }
                    else
                        await item.DownloadCommand.ExecuteAsync(null);
                }
                finally
                {
                    progressCts.Cancel();
                    try { await progressPump; } catch { }
                }
                item.RefreshLocalState();
                if (item.LocalPath == null)
                    throw new IsoException(string.IsNullOrEmpty(item.ErrorText) ? "下载未完成。" : item.ErrorText);
                isoPath = item.LocalPath;
            }

            // 3) Create VM
            Phase = OneClickPhase.Creating;
            StatusText = "正在创建虚拟机…";
            Progress = 100;
            var name = UniqueName(VmName);
            var req = BuildRequest(item.Entry, name, isoPath);
            HyperVService.ValidateCreateRequest(req, checkIsoExists: OperatingSystem.IsWindows());
            if (await _hv.VmNameExistsAsync(name))
                throw new IsoException($"已存在同名虚拟机「{name}」。");
            await _hv.CreateVmAsync(req, new Progress<string>(s => StatusText = s), ct);
            _prefs.RememberIso(isoPath);
            _prefs.LastCpu = req.ProcessorCount;
            _prefs.LastMemoryGiB = Math.Max(1, req.MemoryMiB / 1024);
            _prefs.LastDiskGiB = req.DiskGiB;
            _prefs.LastSwitch = string.IsNullOrEmpty(req.SwitchName) ? null : req.SwitchName;
            _prefs.Save();
            CreatedName = name;
            _existing.Add(name);

            if (EnableBasicEnv)
            {
                try
                {
                    var folder = FirstBootProfile.Write(name, new FirstBootProfile.Options
                    {
                        Os = item.Entry.Os,
                        EnableRdp = EnableRdp && item.Entry.Os == OsKind.Windows,
                        EnableOpenSsh = EnableOpenSsh,
                        AgentReady = AgentReadyProfile,
                        Hostname = string.IsNullOrWhiteSpace(GuestHostname) ? name : GuestHostname.Trim(),
                        Timezone = "Asia/Shanghai",
                        Username = GuestUsername.Trim(),
                        Password = string.IsNullOrEmpty(GuestPassword) ? null : GuestPassword
                    });
                    SetupFolderHint = folder;
                    if (!string.IsNullOrWhiteSpace(GuestUsername))
                    {
                        _prefs.LastGuestUser = GuestUsername.Trim();
                        _prefs.LastGuestHostname = string.IsNullOrWhiteSpace(GuestHostname) ? name : GuestHostname.Trim();
                        _prefs.PreferAgentReadyProfile = AgentReadyProfile;
                        _prefs.Save();
                    }
                    // Clear password from memory after writing scripts
                    GuestPassword = "";
                    Log.Info($"First-boot scripts written under Setup folder for {name}");
                }
                catch (Exception ex)
                {
                    Log.Warn("First-boot script write failed: " + ex.Message);
                }
            }

            if (StartAfterCreate)
            {
                Phase = OneClickPhase.Starting;
                StatusText = $"正在启动「{name}」…";
                await _hv.StartAsync(name);
                if (OpenConsoleAfterStart)
                {
                    Phase = OneClickPhase.Connecting;
                    StatusText = "正在打开 Hyper-V 连接…";
                    try { await _hv.OpenConsoleAsync(name); }
                    catch (Exception ex)
                    {
                        Log.Warn($"vmconnect after one-click: {ex.Message}");
                        StatusText = $"虚拟机已启动，但打开控制台失败：{ErrorMapper.Friendly(ex)}";
                    }
                }
            }

            Phase = OneClickPhase.Done;
            StatusText = StartAfterCreate
                ? $"完成：已创建并启动「{name}」"
                : $"完成：已创建「{name}」（未启动）";
            if (!string.IsNullOrEmpty(SetupFolderHint))
                StatusText += "；基础环境脚本已生成（连接后来宾中运行）";
            CloseRequested?.Invoke(true);
        }
        catch (OperationCanceledException)
        {
            Phase = OneClickPhase.Failed;
            ErrorText = "已取消";
            StatusText = "已取消";
            try { Selected?.CancelDownloadCommand.Execute(null); } catch { }
        }
        catch (Exception ex)
        {
            Phase = OneClickPhase.Failed;
            ErrorText = ErrorMapper.Friendly(ex);
            StatusText = "一键安装失败";
            Log.Warn($"One-click install failed: {ex}");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            NotifyCan();
        }
    }

    public CreateVmRequest BuildRequest(IsoEntry e, string name, string isoPath) => new()
    {
        Name = name.Trim(),
        ProcessorCount = Math.Clamp(e.RecommendedCpu, 1, Math.Max(1, Environment.ProcessorCount)),
        MemoryMiB = Math.Clamp(e.RecommendedMemoryMiB, 1024, 256 * 1024),
        DiskGiB = Math.Clamp(e.RecommendedDiskGiB, 8, 4096),
        IsoPath = isoPath,
        Generation = 2,
        SwitchName = SwitchName ?? "",
        Os = e.Os,
        RequireTpm = e.RequireTpm || e.Id.Contains("windows-11", StringComparison.OrdinalIgnoreCase)
    };

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            _cts?.Cancel();
            try { Selected?.CancelDownloadCommand.Execute(null); } catch { }
            return;
        }
        CloseRequested?.Invoke(false);
    }

    [RelayCommand]
    private void OpenHomePage()
    {
        if (Selected?.Entry.HomePage is { Length: > 0 } url)
            _library.OpenUrl(url);
    }
}
