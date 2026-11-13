using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Luma.App.Controls;

/// <summary>Centered illustration-free empty / error state: icon, heading, message and optional action content.</summary>
public sealed class EmptyState : ContentControl
{
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<EmptyState, Geometry?>(nameof(Icon));
    public static readonly StyledProperty<string?> HeadingProperty = AvaloniaProperty.Register<EmptyState, string?>(nameof(Heading));
    public static readonly StyledProperty<string?> MessageProperty = AvaloniaProperty.Register<EmptyState, string?>(nameof(Message));
    public static readonly StyledProperty<bool> IsDangerProperty = AvaloniaProperty.Register<EmptyState, bool>(nameof(IsDanger));

    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Heading { get => GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public string? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public bool IsDanger { get => GetValue(IsDangerProperty); set => SetValue(IsDangerProperty, value); }

    protected override Type StyleKeyOverride => typeof(EmptyState);
}

/// <summary>One row of a settings group: title and description on the left, the editing control (content) on the right.</summary>
public sealed class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> HeadingProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Heading));
    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));
    public static readonly StyledProperty<bool> StackedProperty = AvaloniaProperty.Register<SettingRow, bool>(nameof(Stacked));

    public string? Heading { get => GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// <summary>Places the content below the text instead of beside it (for wide controls such as text boxes).</summary>
    public bool Stacked { get => GetValue(StackedProperty); set => SetValue(StackedProperty, value); }

    protected override Type StyleKeyOverride => typeof(SettingRow);
}
