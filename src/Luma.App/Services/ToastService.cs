using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Luma.App.Services;

public sealed partial class ToastViewModel : ObservableObject
{
    private readonly Action<ToastViewModel> _dismiss;

    public ToastViewModel(string message, ToastKind kind, string? actionText, Action? action, Action<ToastViewModel> dismiss)
    {
        Message = message;
        Kind = kind;
        ActionText = actionText;
        Action = action;
        _dismiss = dismiss;
    }

    public string Message { get; }
    public ToastKind Kind { get; }
    public string? ActionText { get; }
    public Action? Action { get; }
    public bool HasAction => Action is not null && !string.IsNullOrEmpty(ActionText);

    public bool IsError => Kind == ToastKind.Error;
    public bool IsSuccess => Kind == ToastKind.Success;
    public bool IsWarning => Kind == ToastKind.Warning;
    public bool IsInfo => Kind == ToastKind.Info;

    [RelayCommand]
    private void Dismiss() => _dismiss(this);

    [RelayCommand]
    private void Invoke()
    {
        Action?.Invoke();
        _dismiss(this);
    }
}

/// <summary>Transient, non-blocking notifications shown in the corner of the window.</summary>
public sealed class ToastService : IToastService
{
    private const int MaxVisible = 4;
    private readonly ObservableCollection<ToastViewModel> _toasts = [];
    private (string Message, DateTime At)? _last;

    public ObservableCollection<ToastViewModel> Toasts => _toasts;

    public void Show(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_last is { } last && last.Message == message && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(1.5)) return;
            _last = (message, DateTime.UtcNow);

            var toast = new ToastViewModel(message, kind, actionText, action, Remove);
            _toasts.Add(toast);
            while (_toasts.Count > MaxVisible) _toasts.RemoveAt(0);

            var lifetime = kind == ToastKind.Error ? 8 : action is not null ? 6.5 : 4.5;
            _ = DismissLaterAsync(toast, TimeSpan.FromSeconds(lifetime));
        });
    }

    private async Task DismissLaterAsync(ToastViewModel toast, TimeSpan delay)
    {
        await Task.Delay(delay);
        Dispatcher.UIThread.Post(() => Remove(toast));
    }

    private void Remove(ToastViewModel toast) => _toasts.Remove(toast);
}
