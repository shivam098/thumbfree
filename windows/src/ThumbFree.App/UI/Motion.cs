using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ThumbFree.App.UI;

/// <summary>
/// Small, quick animations that make the app feel smooth: fades, a short slide for pages, colour transitions on hover,
/// and eased mouse-wheel scrolling. Everything is skipped when Windows' "Animation effects" setting is off.
/// </summary>
internal static class Motion
{
    private static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Turns animations off for offscreen renders and tests, where no frames are drawn to move them.</summary>
    public static bool Disabled { get; set; }

    public static bool Enabled => !Disabled && SystemParameters.ClientAreaAnimation;

    /// <summary>Fades an element in (and, with <paramref name="rise"/>, slides it up a few pixels).</summary>
    public static void FadeIn(UIElement element, int milliseconds = 180, double rise = 0)
    {
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Ease });
        if (rise != 0)
        {
            var move = new TranslateTransform(0, rise);
            element.RenderTransform = move;
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, TimeSpan.FromMilliseconds(milliseconds + 60)) { EasingFunction = Ease });
        }
    }

    /// <summary>A brush whose colour can be animated (theme brushes are frozen).</summary>
    public static SolidColorBrush Live(Color color) => new(color);

    public static void ToColor(SolidColorBrush brush, Color color, int milliseconds = 140)
    {
        if (!Enabled)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = color;
            return;
        }

        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Ease });
    }

    /// <summary>Eases a progress bar to its new value instead of jumping.</summary>
    public static void ToValue(System.Windows.Controls.Primitives.RangeBase bar, double value, int milliseconds = 300)
    {
        if (!Enabled)
        {
            bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            bar.Value = value;
            return;
        }

        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Ease });
    }

    /// <summary>
    /// Eased mouse-wheel scrolling instead of jumping three lines at a time. A wheel over a nested scrollable box
    /// (a multi-line text box) scrolls that box while it can.
    /// </summary>
    public static void SmoothScroll(ScrollViewer viewer)
    {
        double target = 0;
        var animating = false;

        void Stop()
        {
            if (animating)
            {
                CompositionTarget.Rendering -= Step;
                animating = false;
            }
        }

        void Step(object? sender, EventArgs e)
        {
            // Off screen (another page is showing) layout doesn't run, so the offset would never arrive: stop.
            if (!viewer.IsLoaded || !viewer.IsVisible)
            {
                Stop();
                return;
            }

            target = Math.Clamp(target, 0, viewer.ScrollableHeight);
            var current = viewer.VerticalOffset;
            var next = current + ((target - current) * 0.28);
            if (Math.Abs(target - next) < 0.6)
            {
                next = target;
                Stop();
            }

            viewer.ScrollToVerticalOffset(next);
        }

        viewer.Unloaded += (_, _) => Stop();
        viewer.ScrollChanged += (_, _) =>
        {
            if (!animating)
            {
                target = viewer.VerticalOffset;
            }
        };
        viewer.PreviewMouseWheel += (_, e) =>
        {
            if (!Enabled || viewer.ScrollableHeight <= 0 || InnerScrollerCanTake(e, viewer))
            {
                return;
            }

            e.Handled = true;
            target = Math.Clamp((animating ? target : viewer.VerticalOffset) - (e.Delta * 0.9), 0, viewer.ScrollableHeight);
            if (!animating)
            {
                animating = true;
                CompositionTarget.Rendering += Step;
            }
        };
    }

    private static bool InnerScrollerCanTake(MouseWheelEventArgs e, ScrollViewer outer)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != outer; node = VisualOrLogicalParent(node))
        {
            if (node is ScrollViewer inner && inner.ScrollableHeight > 0)
            {
                return e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight;
            }
        }

        return false;
    }

    private static DependencyObject? VisualOrLogicalParent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
