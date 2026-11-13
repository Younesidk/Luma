namespace Luma.App.Services;

/// <summary>Decouples "go to page X" requests from the shell, which owns the pages (avoids a dependency cycle).</summary>
public sealed class NavigationService : INavigationService
{
    public event EventHandler<NavigationRequest>? Requested;

    public void NavigateTo(AppPage page) => Requested?.Invoke(this, new NavigationRequest(page, null, null));

    public void ShowDiscover(string sourceId, string? query = null) =>
        Requested?.Invoke(this, new NavigationRequest(AppPage.Discover, sourceId, query));
}

public sealed record NavigationRequest(AppPage Page, string? SourceId, string? Query);
