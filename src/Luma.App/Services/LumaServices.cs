using Microsoft.Extensions.DependencyInjection;

namespace Luma.App.Services;

/// <summary>
/// Narrow service-locator used only by custom controls that the XAML framework constructs (they cannot receive
/// constructor injection). View models and services always use normal dependency injection.
/// </summary>
public static class LumaServices
{
    private static IServiceProvider? _provider;

    public static IServiceProvider Provider => _provider ?? throw new InvalidOperationException("Luma services are not initialised yet.");

    public static void Initialize(IServiceProvider provider) => _provider = provider;

    public static T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
}
