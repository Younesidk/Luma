using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Luma.App.Controls;

/// <summary>
/// Eases mouse-wheel scrolling instead of jumping a fixed step per notch. Precision touchpads already report smooth,
/// fractional deltas, so those (and horizontal or modified scrolls) are left to the ScrollViewer's own handling.
/// </summary>
public static class SmoothScroll
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(SmoothScroll));

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, State?>("State", typeof(SmoothScroll));

    /// <summary>Distance scrolled per wheel notch, in device-independent pixels.</summary>
    private const double StepPerNotch = 96;

    /// <summary>How quickly the offset catches up with the target (higher is snappier).</summary>
    private const double Responsiveness = 16;

    static SmoothScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
        {
            if (e.NewValue is true) viewer.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
            else viewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
        });
    }

    public static bool GetIsEnabled(ScrollViewer element) => element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(ScrollViewer element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.Handled) return;
        var delta = e.Delta.Y;
        if (delta == 0 || e.Delta.X != 0 || e.KeyModifiers != KeyModifiers.None) return;
        // Touchpads send many small fractional deltas that are already smooth; mouse wheels send whole notches.
        if (Math.Abs(delta) < 1 || Math.Abs(delta - Math.Round(delta)) > 0.001) return;

        var max = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        if (max <= 0) return;

        // A scrollable viewer nested inside this one (the event tunnels outer → inner) gets the wheel instead.
        if (e.Source is Visual source && HasScrollableDescendantOnPath(viewer, source)) return;

        var state = viewer.GetValue(StateProperty);
        if (state is null)
        {
            state = new State(viewer);
            viewer.SetValue(StateProperty, state);
        }

        var start = state.IsRunning ? state.Target : viewer.Offset.Y;
        var target = Math.Clamp(start - delta * StepPerNotch, 0, max);
        // Already at the edge: let it bubble so an outer scroller (if any) can take it.
        if (Math.Abs(target - viewer.Offset.Y) < 0.5 && !state.IsRunning) return;

        e.Handled = true;
        state.ScrollTo(target);
    }

    private static bool HasScrollableDescendantOnPath(ScrollViewer viewer, Visual source)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, viewer); current = current.GetVisualParent())
        {
            if (current is ScrollViewer inner && inner.Extent.Height - inner.Viewport.Height > 0.5) return true;
        }
        return false;
    }

    private sealed class State(ScrollViewer viewer)
    {
        private TimeSpan? _lastFrame;
        private double _expected;

        public double Target { get; private set; }

        public bool IsRunning { get; private set; }

        public void ScrollTo(double target)
        {
            Target = target;
            if (IsRunning) return;
            IsRunning = true;
            _lastFrame = null;
            _expected = viewer.Offset.Y;
            RequestFrame();
        }

        private void RequestFrame()
        {
            var top = TopLevel.GetTopLevel(viewer);
            if (top is null)
            {
                Stop(snap: true);
                return;
            }
            top.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan now)
        {
            if (!IsRunning) return;
            var current = viewer.Offset.Y;
            // Someone else moved the offset (scrollbar drag, keyboard, ScrollToHome): give way to them.
            if (Math.Abs(current - _expected) > 1)
            {
                Stop(snap: false);
                return;
            }

            var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0.001, 0.05) : 1.0 / 60;
            _lastFrame = now;

            var max = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            Target = Math.Clamp(Target, 0, max);
            var remaining = Target - current;
            if (Math.Abs(remaining) < 0.5)
            {
                Stop(snap: true);
                return;
            }

            var next = current + remaining * (1 - Math.Exp(-Responsiveness * dt));
            viewer.Offset = new Vector(viewer.Offset.X, next);
            _expected = viewer.Offset.Y;
            RequestFrame();
        }

        private void Stop(bool snap)
        {
            if (snap) viewer.Offset = new Vector(viewer.Offset.X, Target);
            IsRunning = false;
        }
    }
}
