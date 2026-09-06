using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace SemanticStart.App;

/// <summary>
/// Animated mouse-wheel scrolling for a <see cref="ScrollViewer"/> or anything containing one.
///
/// WPF scrolls the wheel in discrete jumps - three items at a time by default, and with a
/// virtualizing panel those items are whole rows, so a result list with expandable detail panels
/// lurches by a variable and often large distance per notch. Two things are needed to fix that:
/// scrolling by pixel rather than by item, which is a property on the panel, and animating between
/// offsets, which is not available at all because <see cref="ScrollViewer.VerticalOffset"/> is
/// read-only.
///
/// So the animation runs on an attached property that forwards each tick to
/// <see cref="ScrollViewer.ScrollToVerticalOffset"/>. Successive notches retarget the same
/// animation from where it has reached rather than restarting it, which is what makes spinning the
/// wheel feel continuous instead of stuttering back to the start of each step.
/// </summary>
public static class SmoothScroll
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(280);

    /// <summary>Pixels travelled per wheel notch. One notch reports 120 units.</summary>
    private const double PixelsPerNotch = 96;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    /// <summary>The animated offset. Private because it exists only to give the animation something to drive.</summary>
    private static readonly DependencyProperty AnimatedOffsetProperty = DependencyProperty.RegisterAttached(
        "AnimatedOffset", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0.0, OnAnimatedOffsetChanged));

    /// <summary>
    /// Where the in-flight animation is heading. Tracked separately from the current offset so a
    /// second notch adds to the destination instead of to wherever the animation happens to be.
    /// </summary>
    private static readonly DependencyProperty TargetOffsetProperty = DependencyProperty.RegisterAttached(
        "TargetOffset", typeof(double), typeof(SmoothScroll), new PropertyMetadata(double.NaN));

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        if ((bool)e.NewValue)
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject root || FindScrollViewer(root) is not { } viewer)
            return;

        if (viewer.ScrollableHeight <= 0)
            return;

        var current = (double)viewer.GetValue(TargetOffsetProperty);
        if (double.IsNaN(current))
            current = viewer.VerticalOffset;

        var target = Math.Clamp(current - e.Delta / 120.0 * PixelsPerNotch, 0, viewer.ScrollableHeight);
        e.Handled = true;

        if (Math.Abs(target - viewer.VerticalOffset) < 0.5)
        {
            viewer.SetValue(TargetOffsetProperty, target);
            return;
        }

        viewer.SetValue(TargetOffsetProperty, target);
        viewer.SetValue(AnimatedOffsetProperty, viewer.VerticalOffset);

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = Duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        // Clearing the target once the animation finishes means the next notch starts from wherever
        // the list actually is, so keyboard navigation and ScrollIntoView between wheel gestures
        // cannot leave a stale destination behind.
        animation.Completed += (_, _) => viewer.SetValue(TargetOffsetProperty, double.NaN);

        viewer.BeginAnimation(AnimatedOffsetProperty, animation);
    }

    private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer viewer)
            viewer.ScrollToVerticalOffset((double)e.NewValue);
    }

    /// <summary>
    /// The ScrollViewer inside a control's template, which does not exist until the template is
    /// applied - so this is resolved per wheel event rather than when the property is attached.
    /// </summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer)
            return viewer;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++)
        {
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(element, i)) is { } found)
                return found;
        }

        return null;
    }
}
