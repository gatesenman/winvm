using WinVM.Models;
using WinVM.Services;

namespace WinVM.ViewModels;

/// <summary>Abstraction over modal dialogs so view-model logic can be unit-tested.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool danger = false);
    Task<(bool Ok, bool Option)> ConfirmWithOptionAsync(string title, string message, string optionText, bool optionDefault, string confirmText, bool danger);
    Task<string?> PromptTextAsync(string title, string message, string initial, string confirmText, string? watermark = null, string? hint = null);
    Task<decimal?> PromptNumberAsync(string title, string message, decimal value, decimal min, decimal max, string unit, string confirmText, string? hint = null);
    /// <summary>Returns the created VM name, or null when cancelled.</summary>
    Task<string?> ShowCreateVmAsync(IHyperVService hv, AppPreferences prefs, ImagePick? initialImage = null);
    Task<bool> ShowSettingsAsync(VmInfo vm, IHyperVService hv, AppPreferences prefs);
    Task ShowErrorAsync(string title, string message);
    Task ShowAboutAsync();
    Task CopyToClipboardAsync(string text);

    /// <summary>Error dialog with an optional longer explanation (e.g. host problems).</summary>
    Task ShowErrorAsync(string title, string message, string? detail) => ShowErrorAsync(title, message);
    /// <summary>Folder picker; null when cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startFolder) => Task.FromResult<string?>(null);
    /// <summary>文件 → 偏好设置. True when the user pressed 确定 (values already applied).</summary>
    Task<bool> ShowPreferencesAsync(PreferencesViewModel vm) => Task.FromResult(false);
    /// <summary>System image gallery. In pick mode returns the chosen image (null when closed).</summary>
    Task<ImagePick?> ShowImageGalleryAsync(ImageLibraryViewModel library, bool pickMode) => Task.FromResult<ImagePick?>(null);
    Task ShowShortcutsAsync() => Task.CompletedTask;
    /// <summary>1.5 虚拟机 → 高级设置. True when changes were saved.</summary>
    Task<bool> ShowAdvancedSettingsAsync(AdvancedSettingsViewModel vm) => Task.FromResult(false);
    /// <summary>1.5 虚拟机 → 虚拟交换机管理.</summary>
    Task ShowSwitchManagerAsync(SwitchManagerViewModel vm) => Task.CompletedTask;
    /// <summary>1.6 一键安装向导. Returns created VM name, or null when cancelled/failed without create.</summary>
    Task<string?> ShowOneClickInstallAsync(IHyperVService hv, AppPreferences prefs, ImageLibraryViewModel library, IsoEntry? initial = null) => Task.FromResult<string?>(null);
}

