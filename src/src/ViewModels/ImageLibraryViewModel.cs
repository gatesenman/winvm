using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinVM.Models;
using WinVM.Services;

namespace WinVM.ViewModels;

/// <summary>Result of picking an image in the gallery.</summary>
public sealed record ImagePick(string IsoPath, IsoEntry? Entry);

/// <summary>The system image gallery. Lives for the whole session (owned by <see cref="MainViewModel"/>), so
/// downloads continue when the gallery window is closed.</summary>
public sealed partial class ImageLibraryViewModel : ViewModelBase
{
    private readonly AppPreferences _prefs;
    private readonly HttpClient _http;
    private readonly IsoDownloader _downloader;
    private bool _resolvedOnce;
    private IsoEntry? _pendingOneClick;

    public ObservableCollection<IsoItemViewModel> LinuxItems { get; } = new();
    public ObservableCollection<IsoItemViewModel> WindowsItems { get; } = new();
    public ObservableCollection<IsoItemViewModel> MacItems { get; } = new();
    public IEnumerable<IsoItemViewModel> AllItems => LinuxItems.Concat(WindowsItems).Concat(MacItems);
    public IEnumerable<IsoItemViewModel> DownloadableItems => AllItems.Where(i => i.Entry.IsDownloadable);

    /// <summary>Raised when a download finishes (or fails) – used for toasts.</summary>
    public event Action<IsoItemViewModel, Exception?>? DownloadFinished;
    /// <summary>Raised when the user chooses an image in the gallery ("使用").</summary>
    public event Action<ImagePick>? Picked;
    /// <summary>Raised when the user asks for one-click install from a gallery card.</summary>
    public event Action<IsoEntry>? OneClickRequested;
    /// <summary>Set by the host: opens a URL in the default browser.</summary>
    public Action<string> OpenUrl { get; set; } = Elevation.OpenUrl;
    /// <summary>Set by the view: file picker for a local ISO.</summary>
    public Func<string?, Task<string?>>? PickLocalIsoAsync { get; set; }

    [ObservableProperty] private bool _isPickMode;

    public string DownloadFolder => _prefs.EffectiveIsoFolder;

    public ImageLibraryViewModel(AppPreferences prefs, HttpClient? http = null, IEnumerable<IsoEntry>? entries = null)
    {
        _prefs = prefs;
        _http = http ?? IsoCatalog.CreateHttpClient();
        _downloader = new IsoDownloader(_http);
        foreach (var e in entries ?? IsoCatalog.Entries)
        {
            var item = new IsoItemViewModel(e, this);
            if (e.IsUnsupported) MacItems.Add(item);
            else if (e.Os == OsKind.Windows) WindowsItems.Add(item);
            else LinuxItems.Add(item);
        }
    }

    public int ActiveDownloads => AllItems.Count(i => i.IsDownloading);

    /// <summary>Resolves current versions (once per session, or again on demand).</summary>
    public async Task EnsureResolvedAsync(bool force = false)
    {
        if (_resolvedOnce && !force) { foreach (var i in AllItems) i.RefreshLocalState(); return; }
        _resolvedOnce = true;
        await Task.WhenAll(DownloadableItems.Where(i => !i.IsDownloading).Select(i => i.ResolveAsync(_http)));
    }

    [RelayCommand]
    private Task RefreshAsync() => EnsureResolvedAsync(force: true);

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        Directory.CreateDirectory(DownloadFolder);
        Elevation.OpenFolder(DownloadFolder);
    }

    [RelayCommand]
    private async Task UseLocalIsoAsync()
    {
        if (PickLocalIsoAsync == null) return;
        var path = await PickLocalIsoAsync(_prefs.LastIsoFolder ?? DownloadFolder);
        if (!string.IsNullOrEmpty(path)) Picked?.Invoke(new ImagePick(path, null));
    }

    internal IsoDownloader Downloader => _downloader;
    internal void RaisePicked(ImagePick p) => Picked?.Invoke(p);
    internal void RaiseOneClick(IsoEntry e)
    {
        _pendingOneClick = e;
        OneClickRequested?.Invoke(e);
    }

    /// <summary>Consumes a one-click request raised while the gallery was open (null if none).</summary>
    public IsoEntry? ConsumeOneClickRequest()
    {
        var e = _pendingOneClick;
        _pendingOneClick = null;
        return e;
    }
    internal void RaiseFinished(IsoItemViewModel item, Exception? ex)
    {
        OnPropertyChanged(nameof(ActiveDownloads));
        DownloadFinished?.Invoke(item, ex);
    }
    internal void RaiseStarted() => OnPropertyChanged(nameof(ActiveDownloads));

    public void CancelAll()
    {
        foreach (var i in AllItems) i.CancelDownloadCommand.Execute(null);
    }
}

/// <summary>One gallery card.</summary>
public sealed partial class IsoItemViewModel : ViewModelBase
{
    private readonly ImageLibraryViewModel _owner;
    private CancellationTokenSource? _cts;

    public IsoEntry Entry { get; }
    public string Name => Entry.Name;
    public string Family => Entry.Family;
    public string Description => Entry.Description;
    public bool IsLinkOnly => Entry.IsLinkOnly;
    public bool IsUnsupported => Entry.IsUnsupported;
    public bool IsWindows => Entry.Os == OsKind.Windows;
    public bool IsMac => Entry.IsUnsupported || Entry.Family.Equals("macOS", StringComparison.OrdinalIgnoreCase);
    public string Monogram => Family switch { "Linux Mint" => "Mint", "AlmaLinux" => "Alma", "macOS" => "Mac", _ => Family };

    [ObservableProperty] private ResolvedIso? _resolved;
    [ObservableProperty] private bool _isResolving;
    [ObservableProperty] private string _resolveError = "";
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;          // 0..100
    [ObservableProperty] private bool _isVerifying;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _localPath;
    [ObservableProperty] private long _partialBytes;
    [ObservableProperty] private string _lastError = "";

    public IsoItemViewModel(IsoEntry entry, ImageLibraryViewModel owner)
    {
        Entry = entry;
        _owner = owner;
    }

    public string VersionText => Resolved != null ? Resolved.Version
        : IsResolving ? "正在获取最新版本…"
        : IsUnsupported ? "不支持"
        : IsLinkOnly ? "官方下载页"
        : "";
    public string SizeText => Resolved?.Size is long s and > 0 ? VmInfo.FormatBytes(s) : Entry.SizeHint;
    public string FileText => Resolved?.FileName ?? "";
    public bool HasChecksum => Resolved?.Sha256 != null;
    public string ChecksumText => Resolved?.Sha256 is { } h ? $"SHA-256 {h[..12]}…" : "";
    public bool IsDownloaded => LocalPath != null;
    public bool HasPartial => !IsDownloading && !IsDownloaded && PartialBytes > 0;
    public bool CanDownload => Entry.IsDownloadable && Resolved != null && !IsDownloading && !IsDownloaded;
    public string DownloadButtonText => HasPartial ? "继续下载" : "下载";
    public bool CanOneClick => Entry.IsDownloadable && !IsDownloading && (IsDownloaded || Resolved != null || Entry.Resolve != null);
    public string PrimaryActionText => IsDownloaded ? "一键安装" : HasPartial ? "继续并安装" : "一键安装";
    public bool HasError => !string.IsNullOrEmpty(ResolveError) || !string.IsNullOrEmpty(LastError);
    public string ErrorText => !string.IsNullOrEmpty(LastError) ? LastError : ResolveError;
    public string StatusText => IsDownloading ? ProgressText
        : IsDownloaded ? "已下载并通过校验"
        : HasPartial ? $"已下载 {VmInfo.FormatBytes(PartialBytes)}，可继续"
        : "";
    public bool ShowStatus => StatusText.Length > 0;
    public string RecommendText => $"建议 {Entry.RecommendedCpu} 核 · {Entry.RecommendedMemoryMiB / 1024.0:0.#} GiB 内存 · {Entry.RecommendedDiskGiB} GiB 磁盘";

    private string? TargetPath => Resolved == null ? null : Path.Combine(_owner.DownloadFolder, Resolved.FileName);

    private void NotifyAll()
    {
        foreach (var n in new[] { nameof(VersionText), nameof(SizeText), nameof(FileText), nameof(HasChecksum), nameof(ChecksumText),
                     nameof(IsDownloaded), nameof(HasPartial), nameof(CanDownload), nameof(DownloadButtonText), nameof(HasError),
                     nameof(ErrorText), nameof(StatusText), nameof(ShowStatus), nameof(CanOneClick), nameof(PrimaryActionText) })
            OnPropertyChanged(n);
        DownloadCommand.NotifyCanExecuteChanged();
        UseCommand.NotifyCanExecuteChanged();
        OneClickInstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnResolvedChanged(ResolvedIso? value) { RefreshLocalState(); NotifyAll(); }
    partial void OnIsResolvingChanged(bool value) => NotifyAll();
    partial void OnResolveErrorChanged(string value) => NotifyAll();
    partial void OnIsDownloadingChanged(bool value) => NotifyAll();
    partial void OnLocalPathChanged(string? value) => NotifyAll();
    partial void OnPartialBytesChanged(long value) => NotifyAll();
    partial void OnLastErrorChanged(string value) => NotifyAll();
    partial void OnProgressTextChanged(string value) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(ShowStatus)); }

    public void RefreshLocalState()
    {
        if (TargetPath is not { } t) { LocalPath = null; PartialBytes = 0; return; }
        LocalPath = IsoDownloader.IsVerified(t, Resolved?.Sha256) ? t : null;
        PartialBytes = LocalPath == null ? IsoDownloader.PartialBytes(t) : 0;
    }

    public async Task ResolveAsync(HttpClient http)
    {
        if (Entry.Resolve == null || IsResolving) return;
        IsResolving = true;
        ResolveError = "";
        try
        {
            Resolved = await Entry.Resolve(http, CancellationToken.None);
            Log.Info($"ISO resolved {Entry.Id}: {Resolved.FileName} size={Resolved.Size} sha={(Resolved.Sha256 != null)}");
        }
        catch (Exception ex)
        {
            ResolveError = ex is IsoException ? ex.Message : "无法获取最新版本：" + ex.Message;
            Log.Warn($"ISO resolve {Entry.Id} failed: {ex.Message}");
        }
        finally
        {
            IsResolving = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        if (Resolved is not { } r || TargetPath is not { } target) return;
        _cts = new CancellationTokenSource();
        LastError = "";
        IsDownloading = true;
        Progress = 0;
        ProgressText = "正在连接…";
        _owner.RaiseStarted();
        Exception? failure = null;
        try
        {
            var progress = new Progress<DownloadProgress>(OnProgress);
            await Task.Run(() => _owner.Downloader.DownloadAsync(r.Url, target, r.Sha256, progress, _cts.Token));
            LocalPath = target;
        }
        catch (OperationCanceledException)
        {
            failure = new OperationCanceledException("已暂停下载");
        }
        catch (Exception ex)
        {
            failure = ex;
            LastError = ex is IsoException ? ex.Message : "下载失败：" + ex.Message;
            Log.Warn($"ISO download {Entry.Id} failed: {ex}");
        }
        finally
        {
            IsDownloading = false;
            IsVerifying = false;
            ProgressText = "";
            _cts.Dispose();
            _cts = null;
            RefreshLocalState();
            _owner.RaiseFinished(this, failure);
        }
    }

    private void OnProgress(DownloadProgress p)
    {
        IsVerifying = p.Stage == DownloadStage.Verifying;
        Progress = p.Fraction < 0 ? 0 : p.Fraction * 100;
        ProgressText = p.Stage switch
        {
            DownloadStage.Connecting => "正在连接…",
            DownloadStage.Verifying => $"正在校验 SHA-256… {Progress:0}%",
            DownloadStage.Completed => "完成",
            _ => FormatTransfer(p)
        };
    }

    internal static string FormatTransfer(DownloadProgress p)
    {
        var got = VmInfo.FormatBytes(p.Received);
        var tot = p.Total is long t and > 0 ? VmInfo.FormatBytes(t) : "?";
        var pct = p.Fraction >= 0 ? $"{p.Fraction * 100:0}% · " : "";
        var speed = p.BytesPerSecond > 0 ? $" · {FormatSpeed(p.BytesPerSecond)}" : "";
        var eta = p.BytesPerSecond > 1 && p.Total is long tt && tt > p.Received
            ? " · 剩余 " + FormatEta(TimeSpan.FromSeconds((tt - p.Received) / p.BytesPerSecond)) : "";
        return $"{pct}{got} / {tot}{speed}{eta}";
    }

    internal static string FormatSpeed(double bps) =>
        bps >= 1024 * 1024 ? $"{bps / 1024 / 1024:0.0} MB/s" : $"{bps / 1024:0} KB/s";

    internal static string FormatEta(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒" : $"{Math.Max(1, t.Seconds)} 秒";

    [RelayCommand]
    private void CancelDownload() => _cts?.Cancel();

    private bool CanUse() => IsDownloaded;

    [RelayCommand(CanExecute = nameof(CanUse))]
    private void Use()
    {
        if (LocalPath != null) _owner.RaisePicked(new ImagePick(LocalPath, Entry));
    }

    [RelayCommand(CanExecute = nameof(CanOneClick))]
    private void OneClickInstall() => _owner.RaiseOneClick(Entry);

    [RelayCommand]
    private void OpenHomePage()
    {
        if (!string.IsNullOrEmpty(Entry.HomePage)) _owner.OpenUrl(Entry.HomePage);
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (LocalPath != null) Elevation.OpenFolder(Path.GetDirectoryName(LocalPath)!);
    }
}
