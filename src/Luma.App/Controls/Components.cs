using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Controls;

/// <summary>
/// Centered illustration-free empty / error state: icon badge, heading, message and optional action content.
/// Template: implicit style in Styles/Components.xaml.
/// </summary>
public sealed partial class EmptyState : ContentControl
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
        nameof(Heading), typeof(string), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata(null));

    public static readonly DependencyProperty IsDangerProperty = DependencyProperty.Register(
        nameof(IsDanger), typeof(bool), typeof(EmptyState), new PropertyMetadata(false, (d, _) => ((EmptyState)d).UpdateStates()));

    /// <summary>Icon path data, e.g. <c>{StaticResource Icon.WifiOff}</c>.</summary>
    public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public string? Heading { get => (string?)GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }

    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }

    /// <summary>Shows the badge in the error colours.</summary>
    public bool IsDanger { get => (bool)GetValue(IsDangerProperty); set => SetValue(IsDangerProperty, value); }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateStates();
    }

    private void UpdateStates() => VisualStateManager.GoToState(this, IsDanger ? "Danger" : "Normal", false);
}

/// <summary>
/// One row of a settings group: title and description on the left, the editing control (content) on the right.
/// The last row of a panel drops its bottom separator automatically. Template: Styles/Components.xaml.
/// </summary>
public sealed partial class SettingRow : ContentControl
{
    public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
        nameof(Heading), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(null, (d, _) => ((SettingRow)d).UpdateStates()));

    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(
        nameof(Stacked), typeof(bool), typeof(SettingRow), new PropertyMetadata(false, (d, _) => ((SettingRow)d).UpdateStates()));

    public SettingRow()
    {
        Loaded += (_, _) => UpdateStates();
    }

    public string? Heading { get => (string?)GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }

    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// <summary>Places the content below the text instead of beside it (for wide controls such as text boxes).</summary>
    public bool Stacked { get => (bool)GetValue(StackedProperty); set => SetValue(StackedProperty, value); }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateStates();
    }

    private void UpdateStates()
    {
        VisualStateManager.GoToState(this, Stacked ? "Stacked" : "Inline", false);
        VisualStateManager.GoToState(this, string.IsNullOrEmpty(Description) ? "NoDescription" : "WithDescription", false);
        var isLast = Parent is Panel panel && panel.Children.Count > 0 && ReferenceEquals(panel.Children[^1], this);
        VisualStateManager.GoToState(this, isLast ? "NoSeparator" : "WithSeparator", false);
    }
}
