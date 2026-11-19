using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WinVM.Models;

public enum ToastKind { Success, Info, Warning, Error }

/// <summary>A transient in-app notification (bottom-right of the main window).</summary>
public sealed partial class ToastItem : ObservableObject
{
    public ToastKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string? ActionText { get; init; }
    public Action? Action { get; init; }
    public DateTime Created { get; } = DateTime.Now;
    internal Action<ToastItem>? Dismiss { get; set; }

    public bool HasAction => !string.IsNullOrEmpty(ActionText) && Action != null;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool IsSuccess => Kind == ToastKind.Success;
    public bool IsInfo => Kind == ToastKind.Info;
    public bool IsWarning => Kind == ToastKind.Warning;
    public bool IsError => Kind == ToastKind.Error;

    [RelayCommand]
    private void Close() => Dismiss?.Invoke(this);

    [RelayCommand]
    private void RunAction()
    {
        try { Action?.Invoke(); } finally { Dismiss?.Invoke(this); }
    }
}
