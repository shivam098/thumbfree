using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ThumbFree.App.Tests;

/// <summary>
/// One STA thread with a dispatcher for every UI test: WPF objects belong to the thread that made them. Windows are
/// never shown; pages are measured, arranged and drawn into bitmaps.
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Ui = new(Start);

    public static void Run(Action action)
    {
        ExceptionDispatchInfo? error = null;
        Ui.Value.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        });
        error?.Throw();
    }

    /// <summary>Lets posted work (store events, layout) run before going on.</summary>
    public static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            // Registers pack:// URIs for the app's images; it's never run, so no window ever opens.
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            UI.Motion.Disabled = true;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "UI tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}

/// <summary>
/// Draws elements offscreen. With THUMBFREE_UI_SHOTS set (to a folder, or 1 for %TEMP%\thumbfree-ui) the drawings are
/// saved as PNGs to look at; otherwise they're only checked for size.
/// </summary>
internal static class Render
{
    public static string? Folder { get; } = Environment.GetEnvironmentVariable("THUMBFREE_UI_SHOTS") switch
    {
        null or "" or "0" => null,
        "1" => Path.Combine(Path.GetTempPath(), "thumbfree-ui"),
        var folder => folder,
    };

    /// <summary>Lays out <paramref name="element"/> at <paramref name="width"/> (and its natural height unless given) and draws it.</summary>
    public static BitmapSource Draw(FrameworkElement element, double width, double? height = null)
    {
        UiThread.DoEvents();
        element.Measure(new Size(width, height ?? double.PositiveInfinity));
        var h = height ?? Math.Ceiling(element.DesiredSize.Height);
        element.Arrange(new Rect(0, 0, width, h));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    public static void Save(FrameworkElement element, string name, double width, double? height = null)
    {
        var bitmap = Draw(element, width, height);
        Assert.True(bitmap.PixelHeight >= 100, $"{name} drew only {bitmap.PixelHeight} px");
        if (Folder is null)
        {
            return;
        }

        Directory.CreateDirectory(Folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Folder, name + ".png"));
        encoder.Save(file);
    }

    /// <summary>Every text shown under <paramref name="root"/> (after layout).</summary>
    public static List<string> Texts(DependencyObject root) => All<TextBlock>(root).Select(t => t.Text).Where(t => t.Length > 0).ToList();

    public static IEnumerable<T> All<T>(DependencyObject root)
        where T : DependencyObject
    {
        // Templates and new content only turn into visuals during layout.
        (root as UIElement)?.UpdateLayout();
        return Descendants<T>(root);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>The control screen readers know as <paramref name="name"/>.</summary>
    public static T Named<T>(DependencyObject root, string name)
        where T : FrameworkElement =>
        All<T>(root).First(e => System.Windows.Automation.AutomationProperties.GetName(e) == name);

    public static Button ButtonWith(DependencyObject root, string text) =>
        All<Button>(root).First(b => Shown(b) && (b.Content as string == text || Texts(b).Contains(text)));

    /// <summary>Visible as far as layout goes (IsVisible needs a window on screen, which these tests never have).</summary>
    public static bool Shown(UIElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    public static void Click(ButtonBase button)
    {
        if (button is ToggleButton toggle)
        {
            toggle.IsChecked = toggle.IsChecked != true;
        }

        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }
}
