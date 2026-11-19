using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinVM.Models;
using WinVM.Services;

namespace WinVM.ViewModels;

/// <summary>虚拟机 → 虚拟交换机管理: list / create / rename / delete switches and change a VM's switch.</summary>
public partial class SwitchManagerViewModel : ViewModelBase
{
    private readonly IHyperVService _hv;
    private readonly IDialogService _dialogs;

    public static readonly string[] KindNames = { "外部（桥接物理网卡）", "内部（主机 ↔ 虚拟机）", "专用（仅虚拟机之间）" };
    public const string Disconnected = "（未连接）";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private VSwitchInfo? _selectedSwitch;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private int _newKindIndex = 1;
    [ObservableProperty] private NetAdapterInfo? _selectedAdapter;
    [ObservableProperty] private bool _allowManagementOs = true;
    [ObservableProperty] private VmInfo? _selectedVm;
    [ObservableProperty] private string? _targetSwitch;

    public ObservableCollection<VSwitchInfo> Switches { get; } = new();
    public ObservableCollection<NetAdapterInfo> Adapters { get; } = new();
    public ObservableCollection<string> SwitchChoices { get; } = new();
    public IReadOnlyList<VmInfo> Vms { get; }
    public IReadOnlyList<string> Kinds => KindNames;
    /// <summary>Something was changed (the main window refreshes when the manager closes).</summary>
    public bool Changed { get; private set; }

    public bool IsExternal => NewKindIndex == 0;
    public bool HasSelection => SelectedSwitch != null;
    public string CreateHint => NewKindIndex switch
    {
        0 => "⚠ 创建外部交换机会把所选物理网卡绑定到 Hyper-V，主机网络会短暂中断（通常几秒，远程桌面/下载可能断开）。",
        1 => "主机与虚拟机可互相访问，不连接外网（可配合 Windows 网络共享/NAT 使用）。",
        _ => "完全隔离：只有连接到同一专用交换机的虚拟机之间可以通信。"
    };

    public SwitchManagerViewModel(IHyperVService hv, IDialogService dialogs, IReadOnlyList<VmInfo> vms, bool autoLoad = true)
    {
        _hv = hv;
        _dialogs = dialogs;
        Vms = vms;
        if (autoLoad) _ = LoadAsync();
    }

    partial void OnNewKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsExternal));
        OnPropertyChanged(nameof(CreateHint));
        if (value == 0 && Adapters.Count == 0) _ = LoadAdaptersAsync();
    }

    partial void OnSelectedSwitchChanged(VSwitchInfo? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVmChanged(VmInfo? value)
    {
        TargetSwitch = value == null ? null : string.IsNullOrEmpty(value.SwitchName) ? Disconnected : value.SwitchName;
        ConnectVmCommand.NotifyCanExecuteChanged();
    }

    partial void OnTargetSwitchChanged(string? value) => ConnectVmCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value)
    {
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
        ConnectVmCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        ErrorText = "";
        try
        {
            var keep = SelectedSwitch?.Id;
            var list = await _hv.ListSwitchesDetailedAsync();
            Switches.Clear();
            foreach (var s in list) Switches.Add(s);
            SelectedSwitch = Switches.FirstOrDefault(s => s.Id == keep) ?? Switches.FirstOrDefault();
            var target = TargetSwitch;
            SwitchChoices.Clear();
            foreach (var s in list) SwitchChoices.Add(s.Name);
            SwitchChoices.Add(Disconnected);
            TargetSwitch = target != null && SwitchChoices.Contains(target) ? target : TargetSwitch;
            StatusText = $"共 {list.Count} 个虚拟交换机";
        }
        catch (Exception ex)
        {
            ErrorText = ErrorMapper.Friendly(ex);
        }
    }

    private async Task LoadAdaptersAsync()
    {
        try
        {
            var list = await _hv.ListPhysicalAdaptersAsync();
            Adapters.Clear();
            foreach (var a in list) Adapters.Add(a);
            SelectedAdapter = Adapters.FirstOrDefault(a => !a.BoundToSwitch && a.Status == "Up") ?? Adapters.FirstOrDefault(a => !a.BoundToSwitch);
        }
        catch (Exception ex)
        {
            ErrorText = ErrorMapper.Friendly(ex);
        }
    }

    private async Task<bool> Run(string label, Func<Task> action, string success)
    {
        IsBusy = true;
        ErrorText = "";
        try
        {
            await action();
            Changed = true;
            StatusText = success;
            Log.Info($"switch manager: {success}");
            await LoadAsync();
            return true;
        }
        catch (Exception ex)
        {
            ErrorText = ErrorMapper.Friendly(ex);
            Log.Warn($"switch manager {label} failed: {ex.Message}");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCreate() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        string name;
        try { name = HyperVService.ValidateSwitchName(NewName); }
        catch (ArgumentException ex) { ErrorText = ex.Message; return; }
        var kind = NewKindIndex switch { 0 => SwitchKind.External, 1 => SwitchKind.Internal, _ => SwitchKind.Private };
        string? adapter = null;
        if (kind == SwitchKind.External)
        {
            if (SelectedAdapter is not { } a) { ErrorText = "请选择要桥接的物理网卡。"; return; }
            if (a.BoundToSwitch) { ErrorText = "该网卡已被另一个外部交换机使用，请选择其他网卡。"; return; }
            adapter = a.Name;
            if (!await _dialogs.ConfirmAsync("创建外部交换机",
                    $"将把网卡「{a.Name}」（{a.Description}）绑定到新的外部交换机「{name}」。\n\n" +
                    "创建过程中主机网络会中断几秒钟，正在进行的下载、远程桌面或会议可能断开。" +
                    (AllowManagementOs ? "" : "\n\n你取消了「允许主机共享此网卡」：创建后主机将无法再通过该网卡上网！") +
                    "\n\n确定继续吗？", "创建", danger: true))
                return;
        }
        if (await Run("create", () => _hv.CreateSwitchAsync(name, kind, adapter, AllowManagementOs), $"已创建虚拟交换机「{name}」"))
        {
            NewName = "";
            SelectedSwitch = Switches.FirstOrDefault(s => s.Name == name) ?? SelectedSwitch;
        }
    }

    private bool CanModify() => !IsBusy && SelectedSwitch is { CanModify: true };

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RenameAsync()
    {
        if (SelectedSwitch is not { } s) return;
        var n = await _dialogs.PromptTextAsync("重命名虚拟交换机", $"为「{s.Name}」输入新名称：", s.Name, "重命名", "新名称",
            s.InUse ? "已连接的虚拟机会自动跟随新名称。" : null);
        if (n == null || n.Trim() == s.Name) return;
        await Run("rename", () => _hv.RenameSwitchAsync(s.Id, n.Trim()), $"已重命名为「{n.Trim()}」");
    }

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task DeleteAsync()
    {
        if (SelectedSwitch is not { } s) return;
        if (s.InUse)
        {
            ErrorText = $"无法删除「{s.Name}」：它正被以下虚拟机使用：{string.Join("、", s.ConnectedVms)}。请先在下方为这些虚拟机更换或断开网络。";
            return;
        }
        var warn = s.Kind == SwitchKind.External ? "\n\n删除外部交换机时主机网络会短暂中断。" : "";
        if (!await _dialogs.ConfirmAsync("删除虚拟交换机", $"确定删除虚拟交换机「{s.Name}」吗？{warn}", "删除", danger: true)) return;
        await Run("delete", () => _hv.DeleteSwitchAsync(s.Id), $"已删除虚拟交换机「{s.Name}」");
    }

    private bool CanConnect() => !IsBusy && SelectedVm != null && TargetSwitch != null &&
                                 TargetSwitch != (string.IsNullOrEmpty(SelectedVm.SwitchName) ? Disconnected : SelectedVm.SwitchName);

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectVmAsync()
    {
        if (SelectedVm is not { } vm || TargetSwitch is not { } t) return;
        var sw = t == Disconnected ? "" : t;
        if (await Run("connect", () => _hv.SetVmSwitchAsync(vm.Id.Length > 0 ? vm.Id : vm.Name, sw),
                sw.Length == 0 ? $"已断开「{vm.Name}」的网络" : $"「{vm.Name}」已连接到「{sw}」"))
        {
            vm.SwitchName = sw;
            ConnectVmCommand.NotifyCanExecuteChanged();
        }
    }
}
