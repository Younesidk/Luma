using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.App.Services;

public abstract partial class DialogViewModel : ObservableObject
{
    public abstract void Cancel();
}

public sealed partial class ConfirmDialogViewModel : DialogViewModel
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfirmDialogViewModel(string title, string message, string confirmText, bool destructive)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        IsDestructive = destructive;
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public bool IsDestructive { get; }
    public bool IsNotDestructive => !IsDestructive;
    public Task<bool> Result => _result.Task;

    [RelayCommand]
    private void Confirm() => _result.TrySetResult(true);

    public override void Cancel() => _result.TrySetResult(false);

    [RelayCommand]
    private void CancelDialog() => Cancel();
}

public sealed partial class CollectionEditDialogViewModel : DialogViewModel
{
    private readonly TaskCompletionSource<CollectionEditResult?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string, string?, Task<string?>>? _validate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _name;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private string? _error;

    public CollectionEditDialogViewModel(string title, string confirmText, string? name, string? description, Func<string, string?, Task<string?>>? validate)
    {
        Title = title;
        ConfirmText = confirmText;
        _name = name ?? string.Empty;
        _description = description ?? string.Empty;
        _validate = validate;
    }

    public string Title { get; }
    public string ConfirmText { get; }
    public Task<CollectionEditResult?> Result => _result.Task;

    partial void OnNameChanged(string value) => Error = null;

    private bool CanConfirm() => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        var name = Name.Trim();
        var description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        if (_validate is not null)
        {
            var problem = await _validate(name, description);
            if (problem is not null)
            {
                Error = problem;
                return;
            }
        }
        _result.TrySetResult(new CollectionEditResult(name, description));
    }

    public override void Cancel() => _result.TrySetResult(null);

    [RelayCommand]
    private void CancelDialog() => Cancel();
}

public sealed partial class CollectionChoiceViewModel : ObservableObject
{
    public CollectionChoiceViewModel(WallpaperCollection collection, bool isMember)
    {
        Collection = collection;
        _isMember = isMember;
    }

    public WallpaperCollection Collection { get; }

    public string CountText => Collection.WallpaperCount == 1 ? "1 wallpaper" : $"{Collection.WallpaperCount} wallpapers";

    [ObservableProperty]
    private bool _isMember;
}

/// <summary>"Add to collection" dialog: toggling a row immediately adds or removes the wallpaper.</summary>
public sealed partial class CollectionPickerDialogViewModel : DialogViewModel
{
    private readonly TaskCompletionSource _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ICollectionService _collections;
    private readonly IToastService _toasts;
    private readonly ILogger _logger;

    public CollectionPickerDialogViewModel(Wallpaper wallpaper, ICollectionService collections, IToastService toasts, ILogger logger)
    {
        Wallpaper = wallpaper;
        _collections = collections;
        _toasts = toasts;
        _logger = logger;
    }

    public Wallpaper Wallpaper { get; }
    public ObservableCollection<CollectionChoiceViewModel> Choices { get; } = [];
    public Task Result => _result.Task;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string? _error;

    public bool IsEmpty => !IsLoading && Choices.Count == 0;

    public async Task LoadAsync()
    {
        try
        {
            var all = await _collections.GetAllAsync();
            var member = await _collections.GetCollectionIdsContainingAsync(Wallpaper.Key);
            Choices.Clear();
            foreach (var c in all) Choices.Add(new CollectionChoiceViewModel(c, member.Contains(c.Id)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load collections");
            Error = "Your collections couldn't be loaded.";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private async Task ToggleAsync(CollectionChoiceViewModel choice)
    {
        try
        {
            if (choice.IsMember)
            {
                await _collections.RemoveWallpaperAsync(choice.Collection.Id, Wallpaper.Key);
                choice.IsMember = false;
            }
            else
            {
                await _collections.AddWallpaperAsync(choice.Collection.Id, Wallpaper);
                choice.IsMember = true;
                _toasts.Show($"Added to \"{choice.Collection.Name}\"", ToastKind.Success);
            }
            Error = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Updating collection membership failed");
            Error = ex is LumaException ? ex.Message : "That change couldn't be saved.";
        }
    }

    private bool CanCreate() => !string.IsNullOrWhiteSpace(NewName);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        try
        {
            var created = await _collections.CreateAsync(NewName, null);
            await _collections.AddWallpaperAsync(created.Id, Wallpaper);
            NewName = string.Empty;
            Error = null;
            _toasts.Show($"Added to new collection \"{created.Name}\"", ToastKind.Success);
            await LoadAsync();
        }
        catch (LumaException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating a collection failed");
            Error = "The collection couldn't be created.";
        }
    }

    [RelayCommand]
    private void Done() => _result.TrySetResult();

    public override void Cancel() => _result.TrySetResult();
}

/// <summary>Shows modal dialogs one at a time on top of the main window.</summary>
public sealed partial class DialogService : ObservableObject, IDialogService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IUiDispatcher _dispatcher;
    private readonly ICollectionService _collections;
    private readonly IToastService _toasts;
    private readonly ILoggerFactory _loggerFactory;

    public DialogService(ICollectionService collections, IToastService toasts, IUiDispatcher dispatcher, ILoggerFactory loggerFactory)
    {
        _dispatcher = dispatcher;
        _collections = collections;
        _toasts = toasts;
        _loggerFactory = loggerFactory;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private DialogViewModel? _current;

    public bool IsOpen => Current is not null;

    public void CancelCurrent() => Current?.Cancel();

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Confirm", bool destructive = false) =>
        ShowAsync(new ConfirmDialogViewModel(title, message, confirmText, destructive), d => d.Result);

    public Task<CollectionEditResult?> EditCollectionAsync(string title, string confirmText, string? name = null, string? description = null,
        Func<string, string?, Task<string?>>? validate = null) =>
        ShowAsync(new CollectionEditDialogViewModel(title, confirmText, name, description, validate), d => d.Result);

    public async Task PickCollectionAsync(Wallpaper wallpaper)
    {
        var dialog = new CollectionPickerDialogViewModel(wallpaper, _collections, _toasts, _loggerFactory.CreateLogger<CollectionPickerDialogViewModel>());
        _ = dialog.LoadAsync();
        await ShowAsync(dialog, async d =>
        {
            await d.Result;
            return true;
        });
    }

    private async Task<TResult> ShowAsync<TDialog, TResult>(TDialog dialog, Func<TDialog, Task<TResult>> getResult) where TDialog : DialogViewModel
    {
        await _gate.WaitAsync();
        try
        {
            await _dispatcher.InvokeAsync(() => Current = dialog);
            return await getResult(dialog);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => Current = null);
            _gate.Release();
        }
    }
}
