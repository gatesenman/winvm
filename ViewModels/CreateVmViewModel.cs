using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinVM.Models;
using WinVM.Services;

namespace WinVM.ViewModels;

public partial class CreateVmViewModel : ViewModelBase
{
    public const string NoSwitch = "（不连接网络）";

    private readonly IHyperVService _hv;
    private readonly AppPreferences _prefs;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _osIndex; // 0 = Linux, 1 = Windows
    [ObservableProperty] private decimal _processorCount = 2;
    [ObservableProperty] private decimal _memoryGiB = 4;
    [ObservableProperty] private decimal _diskGiB = 64;
    [ObservableProperty] private string _isoPath = "";
    [ObservableProperty] private string? _selectedSwitch;
    [ObservableProperty] private IReadOnlyList<string> _switches = new[] { NoSwitch };
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private IReadOnlyList<string> _recentIsos = Array.Empty<string>();

    public decimal MaxCpu { get; } = Math.Max(1, Environment.ProcessorCount);
    public string StorePathText => System.IO.Path.Combine(_hv.StoreRoot, HyperVService.SanitizeName(string.IsNullOrWhiteSpace(Name) ? "<名称>" : Name));
    /// <summary>1.5 presets: 0 自定义, 1 Windows 11, 2 Windows Server, 3 Ubuntu 桌面, 4 通用 Linux.</summary>
    [ObservableProperty] private int _presetIndex;
    public static readonly string[] PresetNames = { "自定义", "Windows 11", "Windows Server", "Ubuntu 桌面", "通用 Linux" };
    public IReadOnlyList<string> Presets => PresetNames;
    public bool IsWindows11 => PresetIndex == 1;
    private bool _applyingPreset;

    partial void OnPresetIndexChanged(int value)
    {
        OnPropertyChanged(nameof(FirmwareText));
        OnPropertyChanged(nameof(IsWindows11));
        _applyingPreset = true;
        try
        {
            switch (value)
            {
                case 1: // Windows 11: TPM 2.0 + Secure Boot required, 4 GiB / 2 vCPU / 64 GiB minimum
                    OsIndex = 1;
                    ProcessorCount = Math.Clamp(Math.Max(2, ProcessorCount), 1, MaxCpu);
                    MemoryGiB = Math.Max(4, MemoryGiB);
                    DiskGiB = Math.Max(64, DiskGiB);
                    break;
                case 2: OsIndex = 1; ProcessorCount = Math.Min(MaxCpu, 2); MemoryGiB = 4; DiskGiB = 127; break;
                case 3: OsIndex = 0; ProcessorCount = Math.Min(MaxCpu, 2); MemoryGiB = 4; DiskGiB = 40; break;
                case 4: OsIndex = 0; ProcessorCount = Math.Min(MaxCpu, 2); MemoryGiB = 2; DiskGiB = 32; break;
            }
        }
        finally { _applyingPreset = false; }
    }

    public string FirmwareText => PresetIndex == 1
        ? "第 2 代（UEFI）· 安全启动：开 · 虚拟 TPM：必须（无法启用时不会创建）· 最低 2 核 / 4 GiB 内存 / 64 GiB 磁盘"
        : OsIndex == 1
        ? "第 2 代（UEFI）· 安全启动：开（Microsoft Windows 模板）· 尝试启用 vTPM"
        : "第 2 代（UEFI）· 安全启动：关（兼容大多数 Linux 发行版）";
    public bool HasRecentIsos => RecentIsos.Count > 0;
    public string? CreatedName { get; private set; }

    /// <summary>Set by the view: opens a file picker and returns the chosen local path.</summary>
    public Func<string?, Task<string?>>? PickIsoAsync { get; set; }
    /// <summary>Set by the dialog service: opens the system image gallery in pick mode.</summary>
    public Func<Task<ImagePick?>>? PickFromGalleryAsync { get; set; }
    public bool HasGallery => PickFromGalleryAsync != null;
    /// <summary>Gallery entry the current ISO came from (sizing/OS were taken from it).</summary>
    [ObservableProperty] private string _imageHint = "";
    private readonly HashSet<string> _existing;
    private string? _imageName; // name we generated from a gallery image (replaced on the next pick unless the user edited it)
    public event Action<bool>? CloseRequested;

    public CreateVmViewModel(IHyperVService hv, AppPreferences prefs, IEnumerable<string>? existingNames = null)
    {
        _hv = hv;
        _prefs = prefs;
        ProcessorCount = Math.Clamp(prefs.LastCpu, 1, (int)MaxCpu);
        MemoryGiB = Math.Clamp(prefs.LastMemoryGiB, 1, 256);
        DiskGiB = Math.Clamp(prefs.LastDiskGiB, 8, 4096);
        RecentIsos = prefs.RecentIsoPaths.Where(p => !OperatingSystem.IsWindows() || File.Exists(p)).ToList();
        var names = new HashSet<string>(existingNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _existing = names;
        var n = "新建虚拟机";
        for (var i = 2; names.Contains(n); i++) n = $"新建虚拟机 {i}";
        Name = n;
        _ = LoadSwitchesAsync();
    }

    partial void OnNameChanged(string value) { OnPropertyChanged(nameof(StorePathText)); ErrorText = ""; }

    private string Unique(string baseName)
    {
        var n = baseName;
        for (var i = 2; _existing.Contains(n); i++) n = $"{baseName} {i}";
        return n;
    }

    /// <summary>Uses an image from the gallery: ISO path, OS, recommended sizing and a matching name.</summary>
    public void ApplyImage(ImagePick pick)
    {
        var defaultName = Name.StartsWith("新建虚拟机", StringComparison.Ordinal) || (_imageName != null && Name == _imageName);
        IsoPath = pick.IsoPath;
        if (pick.Entry is { } e)
        {
            OsIndex = e.Os == OsKind.Windows ? 1 : 0;
            if (e.RequireTpm || e.Name.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)) PresetIndex = 1;
            ProcessorCount = Math.Clamp(e.RecommendedCpu, 1, (int)MaxCpu);
            MemoryGiB = Math.Clamp(e.RecommendedMemoryMiB / 1024m, 1, 256);
            DiskGiB = Math.Clamp(e.RecommendedDiskGiB, 8, 4096);
            if (defaultName)
            {
                var ver = IsoCatalog.VersionFromFile(System.IO.Path.GetFileName(pick.IsoPath.Replace('\\', '/')));
                var baseName = e.Family == "Ubuntu" && e.Id.Contains("server") ? "Ubuntu Server" : e.Family;
                Name = Unique(string.IsNullOrEmpty(ver) ? baseName : $"{baseName} {ver}");
                _imageName = Name;
            }
            ImageHint = $"已按「{e.Name}」的建议配置：{e.RecommendedCpu} 核 · {e.RecommendedMemoryMiB / 1024.0:0.#} GiB 内存 · {e.RecommendedDiskGiB} GiB 磁盘";
        }
        else ImageHint = "";
    }

    [RelayCommand]
    private async Task BrowseGalleryAsync()
    {
        if (PickFromGalleryAsync == null) return;
        var pick = await PickFromGalleryAsync();
        if (pick != null) ApplyImage(pick);
    }
    partial void OnOsIndexChanged(int value)
    {
        // a Linux OS no longer matches a Windows preset (and vice versa)
        if (!_applyingPreset && ((value == 0 && PresetIndex is 1 or 2) || (value == 1 && PresetIndex is 3 or 4))) PresetIndex = 0;
        OnPropertyChanged(nameof(FirmwareText));
    }
    partial void OnRecentIsosChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasRecentIsos));

    partial void OnIsoPathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var guess = OsDetector.Detect(null, null, value);
        if (guess == OsKind.Windows) OsIndex = 1;
        else if (guess == OsKind.Linux) OsIndex = 0;
        if (Name.StartsWith("新建虚拟机", StringComparison.Ordinal))
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(value.Replace('\\', '/'));
            if (!string.IsNullOrWhiteSpace(stem)) Name = stem.Length > 40 ? stem[..40] : stem;
        }
    }

    internal async Task LoadSwitchesAsync()
    {
        try
        {
            var list = (await _hv.ListSwitchesAsync()).ToList();
            list.Add(NoSwitch);
            Switches = list;
            var preferred = _prefs.LastSwitch;
            SelectedSwitch = preferred != null && list.Contains(preferred) ? preferred
                : list.Contains("Default Switch") ? "Default Switch" : list[0];
        }
        catch (Exception ex)
        {
            Switches = new[] { NoSwitch };
            SelectedSwitch = NoSwitch;
            ProgressText = "无法读取虚拟交换机：" + ErrorMapper.Friendly(ex);
        }
    }

    [RelayCommand]
    private async Task RefreshSwitchesAsync()
    {
        ProgressText = "正在刷新虚拟交换机…";
        await LoadSwitchesAsync();
        if (ProgressText.StartsWith("正在", StringComparison.Ordinal))
            ProgressText = $"已找到 {Switches.Count - 1} 个虚拟交换机";
    }

    [RelayCommand]
    private async Task BrowseIsoAsync()
    {
        if (PickIsoAsync == null) return;
        var path = await PickIsoAsync(_prefs.LastIsoFolder);
        if (!string.IsNullOrEmpty(path)) IsoPath = path;
    }

    [RelayCommand]
    private void UseRecentIso(string? path)
    {
        if (!string.IsNullOrEmpty(path)) IsoPath = path;
    }

    [RelayCommand]
    private void ClearIso() => IsoPath = "";

    public CreateVmRequest BuildRequest() => new()
    {
        Name = Name.Trim(),
        ProcessorCount = (int)ProcessorCount,
        MemoryMiB = (int)Math.Round(MemoryGiB * 1024m),
        DiskGiB = (int)DiskGiB,
        IsoPath = IsoPath.Trim(),
        Generation = 2,
        SwitchName = SelectedSwitch is null or NoSwitch ? "" : SelectedSwitch,
        Os = OsIndex == 1 ? OsKind.Windows : OsKind.Linux,
        RequireTpm = PresetIndex == 1
    };

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (IsBusy) return;
        ErrorText = "";
        var req = BuildRequest();
        try
        {
            HyperVService.ValidateCreateRequest(req, checkIsoExists: OperatingSystem.IsWindows());
        }
        catch (ArgumentException ex)
        {
            ErrorText = ex.Message;
            return;
        }

        IsBusy = true;
        ProgressText = "正在检查名称…";
        try
        {
            if (await _hv.VmNameExistsAsync(req.Name))
            {
                ErrorText = $"已存在同名虚拟机「{req.Name}」，请换一个名称。";
                ProgressText = "";
                return;
            }
            await _hv.CreateVmAsync(req, new Progress<string>(s => ProgressText = s));
            if (!string.IsNullOrEmpty(req.IsoPath)) _prefs.RememberIso(req.IsoPath);
            _prefs.LastCpu = req.ProcessorCount;
            _prefs.LastMemoryGiB = (int)MemoryGiB;
            _prefs.LastDiskGiB = req.DiskGiB;
            _prefs.LastSwitch = SelectedSwitch;
            _prefs.Save();
            CreatedName = req.Name;
            Log.Info($"Created VM '{req.Name}' cpu={req.ProcessorCount} mem={req.MemoryMiB}MiB disk={req.DiskGiB}GiB os={req.Os}");
            CloseRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            ErrorText = ErrorMapper.Friendly(ex);
            ProgressText = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (!IsBusy) CloseRequested?.Invoke(false);
    }
}
