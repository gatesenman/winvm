using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinVM.Models;

/// <summary>A long-running operation shown in the task panel (export, import, clone, image download).</summary>
public sealed partial class BackgroundTaskItem : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    public required string Title { get; init; }
    /// <summary>VM key this task locks (actions on that VM are disabled while it runs), if any.</summary>
    public string? VmKey { get; init; }
    public CancellationToken Token => _cts.Token;
    public bool CanCancel { get; init; } = true;
    /// <summary>Extra cancellation hook (e.g. cancel a download owned elsewhere).</summary>
    public Action? OnCancel { get; init; }

    [ObservableProperty] private string _detail = "正在准备…";
    [ObservableProperty] private double _progress;          // 0..100
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private bool _isCancelling;

    public string ProgressText => IsIndeterminate ? "" : $"{Progress:0}%";
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressText));
    partial void OnIsIndeterminateChanged(bool value) => OnPropertyChanged(nameof(ProgressText));

    public void Report(double fraction, string stage)
    {
        if (IsCancelling) return;
        if (fraction < 0) IsIndeterminate = true;
        else { IsIndeterminate = false; Progress = Math.Clamp(fraction * 100, 0, 100); }
        if (!string.IsNullOrEmpty(stage)) Detail = stage;
    }

    [RelayCommand]
    public void Cancel()
    {
        if (!CanCancel || IsCancelling) return;
        IsCancelling = true;
        IsIndeterminate = true;
        Detail = "正在取消…";
        _cts.Cancel();
        try { OnCancel?.Invoke(); } catch { }
    }
}
