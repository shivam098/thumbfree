using System.Windows;
using System.Windows.Interop;

namespace ThumbFree.App.UI;

/// <summary>
/// Keeps the app window and the guide inside the screen's work area (the screen less the taskbar). Their usual 740
/// units are taller than the 672 that a 1920×1080 screen at 150% leaves, so they would open with the title bar or the
/// guide's buttons out of reach. Their content scrolls instead.
/// </summary>
internal static class WindowFit
{
    /// <summary>Space left between a window and each edge of the work area.</summary>
    public const double Margin = 16;

    /// <summary>
    /// The size and minimum size to use in <paramref name="area"/>: as asked, but never more than the work area less
    /// <see cref="Margin"/> on each side.
    /// </summary>
    public static (Size Size, Size Min) Fit(Size size, Size min, Rect area)
    {
        var most = new Size(Math.Max(0, area.Width - (2 * Margin)), Math.Max(0, area.Height - (2 * Margin)));
        var fittedMin = new Size(Math.Min(min.Width, most.Width), Math.Min(min.Height, most.Height));
        var fitted = new Size(
            Math.Clamp(size.Width, fittedMin.Width, most.Width),
            Math.Clamp(size.Height, fittedMin.Height, most.Height));
        return (fitted, fittedMin);
    }

    /// <summary>
    /// Where a window at <paramref name="window"/> goes to be inside <paramref name="area"/>: moved only as far as
    /// needed, and to the area's top left when it's bigger, so the title bar can always be reached.
    /// </summary>
    public static Point Inside(Rect window, Rect area) => new(
        Math.Max(area.Left, Math.Min(window.Left, area.Right - window.Width)),
        Math.Max(area.Top, Math.Min(window.Top, area.Bottom - window.Height)));

    /// <summary>
    /// Sizes <paramref name="window"/> for <paramref name="area"/> (by default the main screen's work area) and, each
    /// time it's shown, fits it again to the work area of the screen it's on and moves it inside.
    /// </summary>
    public static void Apply(Window window, Size size, Size min, Rect? area = null)
    {
        Set(window, size, min, area ?? SystemParameters.WorkArea);
        window.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && window.WindowState == WindowState.Normal)
            {
                var work = area ?? WorkAreaOf(window);
                Set(window, new Size(window.Width, window.Height), min, work);
                if (!double.IsNaN(window.Left) && !double.IsNaN(window.Top))
                {
                    var at = Inside(new Rect(window.Left, window.Top, window.Width, window.Height), work);
                    window.Left = at.X;
                    window.Top = at.Y;
                }
            }
        };
    }

    private static void Set(Window window, Size size, Size min, Rect area)
    {
        var (fitted, fittedMin) = Fit(size, min, area);
        window.MinWidth = fittedMin.Width;
        window.MinHeight = fittedMin.Height;
        window.Width = fitted.Width;
        window.Height = fitted.Height;
    }

    /// <summary>The work area of the screen the window is on, in WPF units (the main screen's until it has a handle).</summary>
    private static Rect WorkAreaOf(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || PresentationSource.FromVisual(window)?.CompositionTarget is not { } target)
        {
            return SystemParameters.WorkArea;
        }

        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var fromDevice = target.TransformFromDevice;
        return new Rect(fromDevice.Transform(new Point(area.Left, area.Top)), fromDevice.Transform(new Point(area.Right, area.Bottom)));
    }
}
