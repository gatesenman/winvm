using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinVM.Models;
using WinVM.Services;

namespace WinVM.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IHyperVService _hv;
    private readonly AppPreferences _prefs;
    private readonly VmInfo _vm;
    private readonly int _origCpu;
    private readonly decimal _origMem;
    private readonly string _origIso;
    private readonly string _origSwitch;

    [ObservableProperty] private decimal _processorCount;
    [ObservableProperty] private decimal _memoryGiB;
    [ObservableProperty] private string _isoPath;
    [ObservableProperty] private string? _selectedSwitch;
    [ObservableProperty] private IReadOnlyList<string> _switches;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorText = "";

    public string VmName => _vm.Name;
    public string Title => $"设置 - {_vm.Name}";
    public string DiskText => _vm.DiskText;
    public string GenerationText => _vm.GenerationText;
    public string VhdPath => string.IsNullOrEmpty(_vm.VhdPath) ? "—" : _vm.VhdPath;
    public decimal MaxCpu { get; } = Math.Max(1, Environment.ProcessorCount);

    public Func<string?, Task<string?>>? PickIsoAsync { get; set; }
    public event Action<bool>? CloseRequested;

    public SettingsViewModel(VmInfo vm, IHyperVService hv, AppPreferences prefs)
    {
        _vm = vm;
        _hv = hv;
        _prefs = prefs;
        _origCpu = Math.Max(1, vm.ProcessorCount);
        _origMem = Math.Max(0.5m, Math.Round(vm.MemoryStartupBytes / (1024m * 1024m * 1024m), 2));
        _origIso = vm.DvdPath ?? "";
        _origSwitch = string.IsNullOrEmpty(vm.SwitchName) ? CreateVmViewModel.NoSwitch : vm.SwitchName;
        _processorCount = _origCpu;
        _memoryGiB = _origMem;
        _isoPath = _origIso;
        _selectedSwitch = _origSwitch;
        _switches = new[] { _origSwitch };
        _ = LoadSwitchesAsync();
    }

    private async Task LoadSwitchesAsync()
    {
        try
        {
            var list = (await _hv.ListSwitchesAsync()).ToList();
            list.Add(CreateVmViewModel.NoSwitch);
            if (!list.Contains(_origSwitch)) list.Insert(0, _origSwitch);
            Switches = list;
            SelectedSwitch = _origSwitch;
        }
        catch { /* keep current */ }
    }

    [RelayCommand]
    private async Task BrowseIsoAsync()
    {
        if (PickIsoAsync == null) return;
        var p = await PickIsoAsync(_prefs.LastIsoFolder);
        if (!string.IsNullOrEmpty(p)) IsoPath = p;
    }

    [RelayCommand]
    private void ClearIso() => IsoPath = "";

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy) return;
        ErrorText = "";
        if (ProcessorCount < 1) { ErrorText = "CPU 至少为 1 个。"; return; }
        if (MemoryGiB < 0.5m) { ErrorText = "内存至少为 0.5 GiB。"; return; }
        if (MemoryGiB > 256) { ErrorText = "内存过大（上限 256 GiB）。"; return; }
        if (!string.IsNullOrWhiteSpace(IsoPath) && OperatingSystem.IsWindows() && !File.Exists(IsoPath))
        {
            ErrorText = "ISO 文件不存在。";
            return;
        }

        int? cpu = (int)ProcessorCount != _origCpu ? (int)ProcessorCount : null;
        long? mem = MemoryGiB != _origMem ? (long)(MemoryGiB * 1024m * 1024m * 1024m) : null;
        string? iso = !string.Equals(IsoPath ?? "", _origIso, StringComparison.OrdinalIgnoreCase) ? (IsoPath ?? "") : null;
        string? sw = SelectedSwitch != _origSwitch ? (SelectedSwitch is null or CreateVmViewModel.NoSwitch ? "" : SelectedSwitch) : null;
        if (cpu == null && mem == null && iso == null && sw == null)
        {
            CloseRequested?.Invoke(false);
            return;
        }

        IsBusy = true;
        try
        {
            await _hv.UpdateSettingsAsync(_vm.Id.Length > 0 ? _vm.Id : _vm.Name, cpu, mem, iso, sw);
            if (!string.IsNullOrWhiteSpace(iso)) _prefs.RememberIso(iso);
            CloseRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            ErrorText = ErrorMapper.Friendly(ex);
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
