using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinVM.Models;
using WinVM.Services;

namespace WinVM.ViewModels;

/// <summary>虚拟机 → 高级设置: secure boot / TPM, memory, boot order, processor, automatic actions, notes, enhanced session.</summary>
public partial class AdvancedSettingsViewModel : ViewModelBase
{
    private const decimal MiB = 1024m * 1024m;
    private readonly IHyperVService _hv;
    private readonly VmInfo _vm;
    private VmAdvancedSettings? _orig;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private int _selectedTab;

    [ObservableProperty] private bool _secureBoot;
    [ObservableProperty] private int _templateIndex;
    [ObservableProperty] private bool _tpm;
    [ObservableProperty] private bool _dynamicMemory;
    [ObservableProperty] private decimal _minimumMiB = 512;
    [ObservableProperty] private decimal _maximumMiB = 1048576;
    [ObservableProperty] private decimal _buffer = 20;
    [ObservableProperty] private BootEntry? _selectedBoot;
    [ObservableProperty] private bool _nested;
    [ObservableProperty] private int _startActionIndex = 1;
    [ObservableProperty] private decimal _startDelay;
    [ObservableProperty] private int _stopActionIndex;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private bool _hvSocket;
    [ObservableProperty] private bool _hostEnhanced;
    [ObservableProperty] private int _generation = 2;
    [ObservableProperty] private VmState _state = VmState.Off;
    [ObservableProperty] private long _startupBytes;

    public ObservableCollection<BootEntry> BootOrder { get; } = new();
    public IReadOnlyList<string> TemplateNames => SecureBootTemplates.Names;
    public IReadOnlyList<string> StartActionNames => AutoStartActions.Names;
    public IReadOnlyList<string> StopActionNames => AutoStopActions.Names;

    public string VmName => _vm.Name;
    public string Title => $"高级设置 - {_vm.Name}";
    public bool IsGen2 => Generation == 2;
    public bool IsOff => State == VmState.Off;
    /// <summary>Firmware/memory/processor/enhanced-session controls (Hyper-V only accepts them while off).</summary>
    public bool CanEditOffOnly => IsOff && !IsBusy && !IsLoading;
    public bool CanEditFirmware => CanEditOffOnly && IsGen2;
    public bool CanEditAlways => !IsBusy && !IsLoading;
    public string StateHint => IsLoading ? "正在读取 Hyper-V 配置…" : IsOff
        ? "虚拟机已关机：所有设置均可修改。"
        : "虚拟机未关机：只能修改「自动操作」与「备注」。其余设置请先关机。";
    public string StartupText => $"启动内存 {StartupBytes / 1024 / 1024:N0} MiB（在「设置」中修改）";
    public string HostEnhancedText => HostEnhanced
        ? "主机已允许增强会话模式。"
        : "主机未启用增强会话模式（Hyper-V 设置 → 增强会话模式策略）。WinVM 不会修改主机设置。";
    public string FirmwareHint => IsGen2 ? "Windows 11 需要同时启用安全启动与 TPM。Linux 请使用「Microsoft UEFI 证书颁发机构」模板或关闭安全启动。"
                                         : "第 1 代（BIOS）虚拟机不支持安全启动、TPM 与此处的启动顺序设置。";
    public bool NestedConflict => Nested && DynamicMemory;

    public event Action<bool>? CloseRequested;

    public AdvancedSettingsViewModel(VmInfo vm, IHyperVService hv, bool autoLoad = true)
    {
        _vm = vm;
        _hv = hv;
        if (autoLoad) _ = LoadAsync();
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorText = "";
        try
        {
            var a = await _hv.GetAdvancedSettingsAsync(_vm.Id.Length > 0 ? _vm.Id : _vm.Name);
            Apply(a);
        }
        catch (Exception ex)
        {
            ErrorText = ErrorMapper.Friendly(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Apply(VmAdvancedSettings a)
    {
        _orig = a;
        Generation = a.Generation;
        State = a.State;
        StartupBytes = a.StartupBytes;
        SecureBoot = a.SecureBoot;
        TemplateIndex = Math.Max(0, Array.IndexOf(SecureBootTemplates.All, a.SecureBootTemplate));
        Tpm = a.TpmEnabled;
        DynamicMemory = a.DynamicMemory;
        MinimumMiB = a.MinimumBytes > 0 ? a.MinimumBytes / MiB : 512;
        MaximumMiB = a.MaximumBytes > 0 ? a.MaximumBytes / MiB : 1048576;
        Buffer = a.MemoryBuffer is >= 5 and <= 2000 ? a.MemoryBuffer : 20;
        BootOrder.Clear();
        foreach (var b in a.BootOrder) BootOrder.Add(b);
        SelectedBoot = BootOrder.FirstOrDefault();
        Nested = a.NestedVirtualization;
        StartActionIndex = Math.Max(0, Array.IndexOf(AutoStartActions.All, a.AutoStartAction));
        StartDelay = a.AutoStartDelaySeconds;
        StopActionIndex = Math.Max(0, Array.IndexOf(AutoStopActions.All, a.AutoStopAction));
        Notes = a.Notes;
        HvSocket = a.EnhancedSessionHvSocket;
        HostEnhanced = a.HostEnhancedSessionEnabled;
    }

    partial void OnIsLoadingChanged(bool value) => RaiseState();
    partial void OnIsBusyChanged(bool value) => RaiseState();
    partial void OnStateChanged(VmState value) => RaiseState();
    partial void OnGenerationChanged(int value) { RaiseState(); OnPropertyChanged(nameof(FirmwareHint)); }
    partial void OnStartupBytesChanged(long value) => OnPropertyChanged(nameof(StartupText));
    partial void OnHostEnhancedChanged(bool value) => OnPropertyChanged(nameof(HostEnhancedText));
    partial void OnNestedChanged(bool value) => OnPropertyChanged(nameof(NestedConflict));
    partial void OnDynamicMemoryChanged(bool value) => OnPropertyChanged(nameof(NestedConflict));
    partial void OnSelectedBootChanged(BootEntry? value) { MoveBootUpCommand.NotifyCanExecuteChanged(); MoveBootDownCommand.NotifyCanExecuteChanged(); }

    private void RaiseState()
    {
        foreach (var n in new[] { nameof(IsGen2), nameof(IsOff), nameof(CanEditOffOnly), nameof(CanEditFirmware), nameof(CanEditAlways), nameof(StateHint) })
            OnPropertyChanged(n);
        MoveBootUpCommand.NotifyCanExecuteChanged();
        MoveBootDownCommand.NotifyCanExecuteChanged();
    }

    private bool CanMoveUp() => CanEditFirmware && SelectedBoot != null && BootOrder.IndexOf(SelectedBoot) > 0;
    private bool CanMoveDown() => CanEditFirmware && SelectedBoot != null && BootOrder.IndexOf(SelectedBoot) is var i && i >= 0 && i < BootOrder.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveBootUp() => MoveBoot(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveBootDown() => MoveBoot(+1);

    /// <summary>Moves an entry to a new position (used by up/down buttons and drag &amp; drop).</summary>
    public void MoveBootTo(BootEntry entry, int newIndex)
    {
        var i = BootOrder.IndexOf(entry);
        if (i < 0 || !CanEditFirmware) return;
        newIndex = Math.Clamp(newIndex, 0, BootOrder.Count - 1);
        if (newIndex == i) return;
        BootOrder.Move(i, newIndex);
        SelectedBoot = entry;
        MoveBootUpCommand.NotifyCanExecuteChanged();
        MoveBootDownCommand.NotifyCanExecuteChanged();
    }

    private void MoveBoot(int delta)
    {
        if (SelectedBoot is { } b) MoveBootTo(b, BootOrder.IndexOf(b) + delta);
    }

    /// <summary>The change relative to what was loaded (null fields = unchanged).</summary>
    public AdvancedSettingsChange BuildChange()
    {
        var o = _orig ?? new VmAdvancedSettings();
        var tpl = SecureBootTemplates.All[Math.Clamp(TemplateIndex, 0, SecureBootTemplates.All.Length - 1)];
        var minB = (long)(MinimumMiB * MiB);
        var maxB = (long)(MaximumMiB * MiB);
        var order = BootOrder.Select(b => b.Index).ToList();
        var origOrder = o.BootOrder.Select(b => b.Index).ToList();
        var dynChanged = DynamicMemory != o.DynamicMemory;
        return new AdvancedSettingsChange
        {
            SecureBoot = SecureBoot != o.SecureBoot ? SecureBoot : null,
            SecureBootTemplate = SecureBoot && tpl != o.SecureBootTemplate ? tpl : null,
            Tpm = Tpm != o.TpmEnabled ? Tpm : null,
            DynamicMemory = dynChanged ? DynamicMemory : null,
            MinimumBytes = DynamicMemory && (dynChanged || HyperVService.AlignMem(minB) != o.MinimumBytes) ? minB : null,
            MaximumBytes = DynamicMemory && (dynChanged || HyperVService.AlignMem(maxB) != o.MaximumBytes) ? maxB : null,
            MemoryBuffer = DynamicMemory && (dynChanged || (int)Buffer != o.MemoryBuffer) ? (int)Buffer : null,
            BootOrder = o.IsGen2 && !order.SequenceEqual(origOrder) ? order : null,
            NestedVirtualization = Nested != o.NestedVirtualization ? Nested : null,
            AutoStartAction = AutoStartActions.All[Math.Clamp(StartActionIndex, 0, 2)] is var sa && sa != o.AutoStartAction ? sa : null,
            AutoStartDelaySeconds = (int)StartDelay != o.AutoStartDelaySeconds ? (int)StartDelay : null,
            AutoStopAction = AutoStopActions.All[Math.Clamp(StopActionIndex, 0, 2)] is var so && so != o.AutoStopAction ? so : null,
            Notes = (Notes ?? "").Trim() != o.Notes.Trim() ? (Notes ?? "").Trim() : null,
            EnhancedSessionHvSocket = HvSocket != o.EnhancedSessionHvSocket ? HvSocket : null,
        };
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy || IsLoading) return;
        ErrorText = "";
        if (_orig == null) { CloseRequested?.Invoke(false); return; }
        var change = BuildChange();
        if (change.IsEmpty) { CloseRequested?.Invoke(false); return; }
        try
        {
            HyperVService.ValidateAdvanced(_orig, change);
        }
        catch (ArgumentException ex)
        {
            ErrorText = ex.Message;
            return;
        }
        IsBusy = true;
        try
        {
            await _hv.ApplyAdvancedSettingsAsync(_vm.Id.Length > 0 ? _vm.Id : _vm.Name, change);
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
