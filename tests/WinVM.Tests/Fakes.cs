using WinVM.Models;
using WinVM.Services;
using WinVM.ViewModels;

namespace WinVM.Tests;

public sealed class FakePsRunner : IPsRunner
{
    public List<(string Script, PsLane Lane)> Calls { get; } = new();
    public Func<string, PsResult> Responder { get; set; } = _ => new PsResult(true, "OK", "");

    public Task<PsResult> RunAsync(string script, TimeSpan timeout, PsLane lane = PsLane.Query, CancellationToken ct = default)
    {
        Calls.Add((script, lane));
        return Task.FromResult(Responder(script));
    }

    public void Dispose() { }
}

public sealed class FakeDialogs : IDialogService
{
    public bool ConfirmResult { get; set; } = true;
    public bool OptionResult { get; set; }
    public string? PromptResult { get; set; } = "x";
    public decimal? NumberResult { get; set; }
    public string? CreateResult { get; set; }
    public List<string> Errors { get; } = new();
    public List<string> Confirms { get; } = new();

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool danger = false)
    { Confirms.Add(title); return Task.FromResult(ConfirmResult); }
    public Task<(bool Ok, bool Option)> ConfirmWithOptionAsync(string title, string message, string optionText, bool optionDefault, string confirmText, bool danger)
    { Confirms.Add(title); return Task.FromResult((ConfirmResult, OptionResult)); }
    public Task<string?> PromptTextAsync(string title, string message, string initial, string confirmText, string? watermark = null, string? hint = null)
        => Task.FromResult(PromptResult);
    public Task<decimal?> PromptNumberAsync(string title, string message, decimal value, decimal min, decimal max, string unit, string confirmText, string? hint = null)
        => Task.FromResult(NumberResult);
    public ImagePick? LastCreateImage { get; private set; }
    public async Task<string?> ShowCreateVmAsync(IHyperVService hv, AppPreferences prefs, ImagePick? initialImage = null)
    {
        LastCreateImage = initialImage;
        if (CreateResult == null) return null;
        await hv.CreateVmAsync(new CreateVmRequest { Name = CreateResult, ProcessorCount = 1, MemoryMiB = 1024, DiskGiB = 8, SwitchName = "" });
        return CreateResult;
    }
    public Task<bool> ShowSettingsAsync(VmInfo vm, IHyperVService hv, AppPreferences prefs) => Task.FromResult(false);
    public Task ShowErrorAsync(string title, string message) { Errors.Add(title + ":" + message); return Task.CompletedTask; }
    public Task ShowAboutAsync() => Task.CompletedTask;
    public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
    public List<string?> ErrorDetails { get; } = new();
    public Task ShowErrorAsync(string title, string message, string? detail)
    { Errors.Add(title + ":" + message); ErrorDetails.Add(detail); return Task.CompletedTask; }
    public string? FolderResult { get; set; }
    public Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult(FolderResult);
    public bool PreferencesResult { get; set; }
    public Func<PreferencesViewModel, bool>? OnPreferences { get; set; }
    public Task<bool> ShowPreferencesAsync(PreferencesViewModel vm) => Task.FromResult(OnPreferences?.Invoke(vm) ?? PreferencesResult);
    public ImagePick? GalleryResult { get; set; }
    public int GalleryShown { get; private set; }
    public Task<ImagePick?> ShowImageGalleryAsync(ImageLibraryViewModel library, bool pickMode) { GalleryShown++; return Task.FromResult(GalleryResult); }
    public IsoEntry? OneClickInitial { get; set; }
    public string? OneClickResult { get; set; }
    public int OneClickShown { get; private set; }
    public Task<string?> ShowOneClickInstallAsync(IHyperVService hv, AppPreferences prefs, ImageLibraryViewModel library, IsoEntry? initial = null)
    {
        OneClickShown++;
        OneClickInitial = initial;
        return Task.FromResult(OneClickResult);
    }
    public int AdvancedShown { get; private set; }
    public Func<AdvancedSettingsViewModel, Task<bool>>? OnAdvanced { get; set; }
    public async Task<bool> ShowAdvancedSettingsAsync(AdvancedSettingsViewModel vm) { AdvancedShown++; return OnAdvanced != null && await OnAdvanced(vm); }
    public int SwitchManagerShown { get; private set; }
    public Func<SwitchManagerViewModel, Task>? OnSwitchManager { get; set; }
    public async Task ShowSwitchManagerAsync(SwitchManagerViewModel vm) { SwitchManagerShown++; if (OnSwitchManager != null) await OnSwitchManager(vm); }
}

internal static class TestPrefs
{
    public static AppPreferences Isolated()
    {
        AppPreferences.StoreDir = Path.Combine(Path.GetTempPath(), "winvm-tests-" + Environment.ProcessId);
        Log.Directory = Path.Combine(AppPreferences.StoreDir, "logs");
        return new AppPreferences();
    }
}
