using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace Luma.App.Controls;

/// <summary>
/// Attached shimmer for loading placeholders: opacity eases 0.55 ↔ 1 every 1.4 s while the element is loaded.
/// Use via <c>SkeletonBorderStyle</c> or <c>c:Skeleton.IsAnimated="True"</c>.
/// </summary>
public static class Skeleton
{
    public static readonly DependencyProperty IsAnimatedProperty = DependencyProperty.RegisterAttached(
        "IsAnimated", typeof(bool), typeof(Skeleton), new PropertyMetadata(false, OnIsAnimatedChanged));

    private static readonly DependencyProperty StoryboardProperty = DependencyProperty.RegisterAttached(
        "Storyboard", typeof(Storyboard), typeof(Skeleton), new PropertyMetadata(null));

    public static bool GetIsAnimated(DependencyObject element) => (bool)element.GetValue(IsAnimatedProperty);

    public static void SetIsAnimated(DependencyObject element, bool value) => element.SetValue(IsAnimatedProperty, value);

    private static void OnIsAnimatedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
        Stop(element);
        if (e.NewValue is not true) return;
        element.Loaded += OnLoaded;
        element.Unloaded += OnUnloaded;
        if (element.IsLoaded) Start(element);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Start((FrameworkElement)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Stop((FrameworkElement)sender);

    private static void Start(FrameworkElement element)
    {
        if (element.GetValue(StoryboardProperty) is Storyboard) return;
        var animation = new DoubleAnimation
        {
            From = 0.55,
            To = 1,
            Duration = TimeSpan.FromSeconds(1.4),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        element.SetValue(StoryboardProperty, storyboard);
        storyboard.Begin();
    }

    private static void Stop(FrameworkElement element)
    {
        if (element.GetValue(StoryboardProperty) is not Storyboard storyboard) return;
        storyboard.Stop();
        element.ClearValue(StoryboardProperty);
    }
}
