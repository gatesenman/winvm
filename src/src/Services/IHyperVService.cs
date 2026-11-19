using WinVM.Models;

namespace WinVM.Services;

public sealed record HyperVStatus(
    bool Available,
    IReadOnlyList<string> Codes,
    string Message,
    bool IsElevated,
    bool CanElevate)
{
    public bool NeedsElevation => !Available && CanElevate && !IsElevated &&
        Codes.Any(c => c is "ACCESS_DENIED" or "NEED_PRIVILEGE" or "NOT_ELEVATED");
}

/// <summary>Progress of a long operation. <paramref name="Fraction"/> is 0..1, or negative when unknown.</summary>
public readonly record struct OpProgress(double Fraction, string Stage);

public interface IHyperVService
{
    string StoreRoot { get; }
    /// <summary>The next query must not use data cached before this call (manual refresh).</summary>
    void RequestFresh() { }
    Task<HyperVStatus> CheckHyperVAsync(CancellationToken ct = default);
    Task<IReadOnlyList<VmInfo>> ListVmsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListSwitchesAsync(CancellationToken ct = default);
    Task<bool> VmNameExistsAsync(string name, CancellationToken ct = default);
    Task CreateVmAsync(CreateVmRequest req, IProgress<string>? progress = null, CancellationToken ct = default);
    Task StartAsync(string name);
    Task StopAsync(string name);
    Task TurnOffAsync(string name);
    Task PauseAsync(string name);
    Task ResumeAsync(string name);
    Task RestartAsync(string name);
    Task ResetAsync(string name);
    Task DeleteAsync(string name, bool deleteFiles);
    Task UpdateSettingsAsync(string name, int? cpu, long? memoryBytes, string? isoPath, string? switchName = null);
    Task RenameAsync(string oldName, string newName);
    Task ResizeVhdAsync(string name, int newSizeGiB);
    Task<IReadOnlyList<CheckpointInfo>> ListCheckpointsAsync(string vmName, CancellationToken ct = default);
    Task<string> CreateCheckpointAsync(string vmName, string? checkpointName);
    Task ApplyCheckpointAsync(string vmName, string checkpointId);
    Task DeleteCheckpointAsync(string vmName, string checkpointId);
    Task OpenConsoleAsync(string name);
    /// <summary>Launch mstsc against a guest IP (RDP). Throws when host is empty.</summary>
    Task OpenRdpAsync(string host, int port = 3389);
    /// <summary>Launch ssh against a guest IP. Throws when host is empty.</summary>
    Task OpenSshAsync(string host, string? user = null, int port = 22);

    /// <summary>Exports a VM (must be off) into <paramref name="parentFolder"/>\&lt;VM name&gt;. Returns that folder.</summary>
    Task<string> ExportVmAsync(string vm, string parentFolder, IProgress<OpProgress>? progress = null, CancellationToken ct = default);
    /// <summary>Imports a copy (new Id) of an exported VM into the store and names it <paramref name="newName"/>. Returns the new Id.</summary>
    Task<string> ImportVmAsync(string sourceFolder, string newName, IProgress<OpProgress>? progress = null, CancellationToken ct = default);
    /// <summary>Clones a VM (must be off): export to a temporary folder, import a copy with a new Id and name. Returns the new Id.</summary>
    Task<string> CloneVmAsync(string vm, string newName, IProgress<OpProgress>? progress = null, CancellationToken ct = default);

    // ---- 1.5: advanced settings, virtual switches
    Task<VmAdvancedSettings> GetAdvancedSettingsAsync(string vm, CancellationToken ct = default);
    /// <summary>Applies only the changed fields. Fields in <see cref="AdvancedSettingsChange.RequiresOff"/> need the VM off.</summary>
    Task ApplyAdvancedSettingsAsync(string vm, AdvancedSettingsChange change);
    Task<IReadOnlyList<VSwitchInfo>> ListSwitchesDetailedAsync(CancellationToken ct = default);
    Task<IReadOnlyList<NetAdapterInfo>> ListPhysicalAdaptersAsync(CancellationToken ct = default);
    Task CreateSwitchAsync(string name, SwitchKind kind, string? adapterName = null, bool allowManagementOs = true);
    Task RenameSwitchAsync(string switchId, string newName);
    /// <summary>Refuses (Validation) when any VM is connected or it is the Default Switch.</summary>
    Task DeleteSwitchAsync(string switchId);
    /// <summary>Connects the VM's first network adapter to <paramref name="switchName"/> (null/"" disconnects). Allowed while running.</summary>
    Task SetVmSwitchAsync(string vm, string? switchName);
}
