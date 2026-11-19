namespace WinVM.Models;

public class CreateVmRequest
{
    public string Name { get; set; } = "";
    public int ProcessorCount { get; set; } = 2;
    public int MemoryMiB { get; set; } = 4096;
    public int DiskGiB { get; set; } = 64;
    /// <summary>Optional. Empty = no installation media.</summary>
    public string IsoPath { get; set; } = "";
    public int Generation { get; set; } = 2;
    /// <summary>Empty = not connected.</summary>
    public string SwitchName { get; set; } = "Default Switch";
    public OsKind Os { get; set; } = OsKind.Linux;
    /// <summary>Folder that will contain &lt;name&gt;\ (VHD + config). Defaults to %USERPROFILE%\WinVM\Machines.</summary>
    public string? StoreRoot { get; set; }
    /// <summary>Windows 11 preset: TPM + Secure Boot are mandatory (creation is rolled back if they cannot be enabled)
    /// and 4 GiB / 2 vCPU / 64 GiB minimums are enforced.</summary>
    public bool RequireTpm { get; set; }
}
