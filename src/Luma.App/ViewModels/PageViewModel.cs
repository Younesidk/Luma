using CommunityToolkit.Mvvm.ComponentModel;

namespace Luma.App.ViewModels;

public enum GalleryState
{
    Idle,
    Loading,
    Loaded,
    Empty,
    Error
}

/// <summary>Base class of every top-level page. Pages are singletons so their state survives navigation.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Called every time the page becomes visible.</summary>
    public virtual Task OnActivatedAsync() => Task.CompletedTask;

    public virtual void OnDeactivated()
    {
    }
}

/// <summary>A labelled value for combo boxes.</summary>
public sealed record Option<T>(string Label, T Value)
{
    public override string ToString() => Label;
}
