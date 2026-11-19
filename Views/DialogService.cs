using Avalonia.Controls;
using Avalonia.Platform.Storage;
using WinVM.Models;
using WinVM.Services;
using WinVM.ViewModels;

namespace WinVM.Views;

/// <summary>Shows real modal windows owned by the main window.</summary>
public sealed class DialogService : IDialogService
{
    private readonly Func<Window?> _owner;

    public DialogService(Func<Window?> owner) => _owner = owner;

    private Window? Owner
    {
        get
        {
            var main = _owner();
            if (main == null) return null;
            // Prefer the top-most open dialog so nested dialogs stack correctly.
            return main.OwnedWindows.LastOrDefault(w => w.IsVisible) ?? main;
        }
    }

    private async Task<T?> Show<T>(Window w)
    {
        var owner = Owner;
        if (owner == null) return default;
        return await w.ShowDialog<T?>(owner);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool danger = false) =>
        await Show<bool>(new ConfirmWindow(title, message, null, false, confirmText, danger));

    public async Task<(bool Ok, bool Option)> ConfirmWithOptionAsync(string title, string message, string optionText, bool optionDefault, string confirmText, bool danger)
    {
        var w = new ConfirmWindow(title, message, optionText, optionDefault, confirmText, danger);
        var ok = await Show<bool>(w);
        return (ok, w.OptionChecked);
    }

    public async Task<string?> PromptTextAsync(string title, string message, string initial, string confirmText, string? watermark = null, string? hint = null)
    {
        var w = new PromptWindow(title, message, initial, confirmText, watermark, hint);
        return await Show<bool>(w) ? w.InputText : null;
    }

    public async Task<decimal?> PromptNumberAsync(string title, string message, decimal value, decimal min, decimal max, string unit, string confirmText, string? hint = null)
    {
        var w = new PromptWindow(title, message, null, confirmText, null, hint, numeric: true, numberValue: value, numberMin: min, numberMax: max, unit: unit);
        return await Show<bool>(w) ? w.NumberValue : null;
    }

    public async Task<string?> ShowCreateVmAsync(IHyperVService hv, AppPreferences prefs, ImagePick? initialImage = null)
    {
        var main = _owner()?.DataContext as MainViewModel;
        var names = main?.Vms.Select(v => v.Name) ?? Array.Empty<string>();
        var vm = new CreateVmViewModel(hv, prefs, names);
        if (main != null) vm.PickFromGalleryAsync = () => ShowImageGalleryAsync(main.Library, pickMode: true);
        if (initialImage != null) vm.ApplyImage(initialImage);
        var ok = await Show<bool>(new CreateVmWindow { DataContext = vm });
        return ok ? vm.CreatedName : null;
    }

    public async Task<ImagePick?> ShowImageGalleryAsync(ImageLibraryViewModel library, bool pickMode)
    {
        library.IsPickMode = pickMode;
        var w = new ImageGalleryWindow { DataContext = library };
        _ = library.EnsureResolvedAsync();
        return await Show<ImagePick>(w);
    }

    public async Task<bool> ShowPreferencesAsync(PreferencesViewModel vm) =>
        await Show<bool>(new PreferencesWindow { DataContext = vm });

    public async Task ShowShortcutsAsync() => await Show<object>(new ShortcutsWindow());

    public async Task<bool> ShowAdvancedSettingsAsync(AdvancedSettingsViewModel vm) =>
        await Show<bool>(new AdvancedSettingsWindow { DataContext = vm });

    public async Task ShowSwitchManagerAsync(SwitchManagerViewModel vm) =>
        await Show<object>(new SwitchManagerWindow { DataContext = vm });

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        var owner = Owner;
        return owner == null ? null : await PickFolderAsync(owner, title, startFolder);
    }

    public static async Task<string?> PickFolderAsync(Window window, string title, string? startFolder)
    {
        IStorageFolder? start = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(startFolder) && Directory.Exists(startFolder))
                start = await window.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        }
        catch { /* ignore */ }
        var r = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start
        });
        return r.Count > 0 ? r[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ShowSettingsAsync(VmInfo vm, IHyperVService hv, AppPreferences prefs) =>
        await Show<bool>(new SettingsWindow { DataContext = new SettingsViewModel(vm, hv, prefs) });

    public async Task ShowErrorAsync(string title, string message) => await ShowErrorAsync(title, message, null);

    public async Task ShowErrorAsync(string title, string message, string? detail)
    {
        var w = new ConfirmWindow(title + "失败", message, null, false, "确定", danger: false, showCancel: false, error: true);
        w.SetDetail(detail);
        await Show<bool>(w);
    }

    public async Task<string?> ShowOneClickInstallAsync(IHyperVService hv, AppPreferences prefs, ImageLibraryViewModel library, IsoEntry? initial = null)
    {
        var main = _owner()?.DataContext as MainViewModel;
        var names = main?.Vms.Select(v => v.Name) ?? Array.Empty<string>();
        var vm = new OneClickInstallViewModel(hv, prefs, library, names);
        if (initial != null) vm.SelectEntry(initial);
        _ = library.EnsureResolvedAsync();
        var ok = await Show<bool>(new OneClickWizardWindow { DataContext = vm });
        return ok ? vm.CreatedName : null;
    }

    public async Task ShowAboutAsync() => await Show<object>(new AboutWindow());

    public async Task CopyToClipboardAsync(string text)
    {
        var cb = _owner()?.Clipboard;
        if (cb != null) await cb.SetTextAsync(text);
    }

    public static async Task<string?> PickIsoAsync(Window window, string? startFolder)
    {
        IStorageFolder? start = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(startFolder) && Directory.Exists(startFolder))
                start = await window.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        }
        catch { /* ignore */ }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择安装镜像（Linux / Windows ISO）",
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ISO 镜像") { Patterns = new[] { "*.iso" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
