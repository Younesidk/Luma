using Avalonia.Controls;
using Avalonia.Input;
using Luma.App.ViewModels;

namespace Luma.App.Views;

public partial class PreviewOverlay : UserControl
{
    public PreviewOverlay()
    {
        InitializeComponent();
        this.FindControl<Grid>("Stage")!.PointerPressed += OnStagePressed;
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    /// <summary>A click on the empty area around the image closes the preview.</summary>
    private void OnStagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not PreviewViewModel vm) return;
        if (!ReferenceEquals(e.Source, sender)) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) vm.CloseCommand.Execute(null);
    }
}

public sealed class FavoriteTextConverter : Avalonia.Data.Converters.IValueConverter
{
    public static FavoriteTextConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is true ? "Favorited" : "Favorite";

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
