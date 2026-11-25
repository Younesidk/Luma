using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Luma.App.Views;

/// <summary>
/// Implemented by page views that own a search box, so the shell's Ctrl+K / Ctrl+F shortcuts can focus it.
/// </summary>
public interface ISearchFocusable
{
    /// <summary>Focuses and selects the page's search box; false when the page has none visible.</summary>
    bool FocusSearch();
}

/// <summary>
/// Implemented by page views that show their own overlay (e.g. the Live wallpapers video preview), so the shell's Esc
/// handling can close it after dialogs and the wallpaper preview had their turn.
/// </summary>
public interface IEscapeHandler
{
    /// <summary>Closes the page's overlay; false when there was nothing to close.</summary>
    bool HandleEscape();
}

/// <summary>Code-behind wiring that compiled bindings cannot express (generic option records, enums, bool? toggles).</summary>
internal static class ViewHelpers
{
    /// <summary>
    /// Shows <paramref name="items"/> in a ComboBox by label and keeps its selection in sync with a view-model property
    /// in both directions. Returns an action that detaches the handlers.
    /// </summary>
    public static Action BindCombo<T>(ComboBox combo, IReadOnlyList<T> items, Func<T, string> label, Func<T> get, Action<T> set,
        INotifyPropertyChanged source, string propertyName)
    {
        var syncing = false;
        combo.ItemsSource = items.Select(label).ToList();

        void Pull()
        {
            syncing = true;
            var current = get();
            var index = -1;
            for (var i = 0; i < items.Count; i++)
            {
                if (EqualityComparer<T>.Default.Equals(items[i], current)) { index = i; break; }
            }
            combo.SelectedIndex = index;
            syncing = false;
        }

        void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (syncing || combo.SelectedIndex < 0 || combo.SelectedIndex >= items.Count) return;
            set(items[combo.SelectedIndex]);
        }

        void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == propertyName) Pull();
        }

        Pull();
        combo.SelectionChanged += OnSelectionChanged;
        source.PropertyChanged += OnSourceChanged;
        return () =>
        {
            combo.SelectionChanged -= OnSelectionChanged;
            source.PropertyChanged -= OnSourceChanged;
        };
    }

    /// <summary>
    /// <see cref="BindCombo{T}"/> over a collection the view model rebuilds (Clear + Add): the labels are rebuilt whenever
    /// the collection changes. Returns an action that detaches everything.
    /// </summary>
    public static Action BindComboLive<T>(ComboBox combo, ObservableCollection<T> items, Func<T, string> label, Func<T> get, Action<T> set,
        INotifyPropertyChanged source, string propertyName)
    {
        Action? detach = null;

        void Rebind()
        {
            detach?.Invoke();
            detach = null;
            if (items.Count == 0)
            {
                // Getters may index the collection, so don't ask for the selection while it is empty.
                combo.ItemsSource = null;
                return;
            }
            detach = BindCombo(combo, items, label, get, set, source, propertyName);
        }

        void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebind();

        Rebind();
        items.CollectionChanged += OnCollectionChanged;
        return () =>
        {
            items.CollectionChanged -= OnCollectionChanged;
            detach?.Invoke();
            detach = null;
        };
    }

    /// <summary>Two-way sync between a Slider and an int view-model property. Returns a detach action.</summary>
    public static Action BindSlider(Slider slider, Func<int> get, Action<int> set, INotifyPropertyChanged source, string propertyName)
    {
        var syncing = false;
        void Pull()
        {
            syncing = true;
            slider.Value = get();
            syncing = false;
        }
        void OnValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (syncing) return;
            var value = (int)Math.Round(e.NewValue);
            if (value != get()) set(value);
        }
        void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == propertyName) Pull();
        }

        Pull();
        slider.ValueChanged += OnValueChanged;
        source.PropertyChanged += OnSourceChanged;
        return () =>
        {
            slider.ValueChanged -= OnValueChanged;
            source.PropertyChanged -= OnSourceChanged;
        };
    }

    /// <summary>Two-way sync between a ToggleButton and a bool view-model property. Returns a detach action.</summary>
    public static Action BindToggle(ToggleButton toggle, Func<bool> get, Action<bool> set, INotifyPropertyChanged source, string propertyName)
    {
        void Pull() => toggle.IsChecked = get();
        void OnClick(object sender, RoutedEventArgs e)
        {
            set(toggle.IsChecked == true);
            // The view model may refuse (e.g. at least one category must stay on): reflect what it actually holds.
            Pull();
        }
        void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == propertyName) Pull();
        }

        Pull();
        toggle.Click += OnClick;
        source.PropertyChanged += OnSourceChanged;
        return () =>
        {
            toggle.Click -= OnClick;
            source.PropertyChanged -= OnSourceChanged;
        };
    }

    /// <summary>Depth-first search of the visual tree for the first descendant matching <paramref name="predicate"/>.</summary>
    public static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && predicate(match)) return match;
            if (FindDescendant(child, predicate) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>Focuses a text box once it is in the tree and selects its text.</summary>
    public static bool FocusAndSelect(TextBox? box)
    {
        if (box is null || box.Visibility != Visibility.Visible || box.XamlRoot is null) return false;
        box.Focus(FocusState.Programmatic);
        box.SelectAll();
        return true;
    }

    /// <summary>Focuses <paramref name="target"/> after its first layout pass (dialogs open their primary field focused).</summary>
    public static void FocusWhenLoaded(Control? target, FrameworkElement owner)
    {
        if (target is null) return;
        void Focus() => owner.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => target.Focus(FocusState.Programmatic));
        if (owner.IsLoaded) Focus();
        else
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                owner.Loaded -= OnLoaded;
                Focus();
            }
            owner.Loaded += OnLoaded;
        }
    }
}
