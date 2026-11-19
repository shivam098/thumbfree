using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ThumbFree.App.UI;

public enum AppPage
{
    Home,
    History,
    Settings,
    Help,
}

/// <summary>
/// The app window (PLAN 4.4, after Voice Assistant's): Home, History, Settings and Help. Closing it only hides it:
/// ThumbFree keeps running in the tray, and Exit in the tray quits.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly MainView _view;
    private readonly Action<string> _balloon;
    private bool _exiting;
    private bool _toldAboutTray;

    /// <param name="balloon">Shows a short tray notification (once: "still running in the tray").</param>
    public MainWindow(AppServices services, Action<string> balloon)
    {
        _balloon = balloon;
        Title = "ThumbFree";
        Width = 1060;
        Height = 740;
        MinWidth = 860;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Background;
        FontFamily = Theme.Font;
        Icon = Theme.AppIcon();
        UseLayoutRounding = true;
        _view = new MainView(services);
        Content = _view;

        // Fade the contents in each time the window opens (from the tray, a second launch, or the guide).
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Motion.FadeIn(_view, 200);
            }
        };
        Closing += OnClosing;
    }

    /// <summary>Lets the window really close (app exit).</summary>
    public void AllowClose() => _exiting = true;

    public void ShowPage(AppPage page) => _view.ShowPage(page);

    /// <summary>Shows and brings the window forward, restoring it if minimised.</summary>
    public void Bring()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!Activate())
        {
            Platform.Win32.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            _balloon("ThumbFree is still running in the tray, so Ctrl+Win keeps working. Right-click its icon › Exit to quit.");
        }
    }
}

/// <summary>The window's content, a sidebar and the current page: its own control so tests can draw it offscreen.</summary>
internal sealed class MainView : UserControl
{
    private static readonly Color NavRest = Color.FromArgb(0, 0xEB, 0xEB, 0xF0);
    private static readonly Color NavSelected = Theme.Color("#E2DEFB");

    private readonly Dictionary<AppPage, (Button Button, FrameworkElement Page)> _pages = [];
    private readonly ContentControl _content = new() { Focusable = false };

    public MainView(AppServices services)
    {
        Home = new HomePage(services);
        History = new HistoryPage(services);
        Settings = new SettingsPage(services);
        Help = new HelpPage(services);

        var nav = new StackPanel { Margin = new Thickness(12, 18, 12, 12) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 22) };
        brand.Children.Add(new Image { Source = Theme.Asset("logo-64.png"), Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) });
        brand.Children.Add(new TextBlock { Text = "ThumbFree", FontSize = 17, FontFamily = Theme.DisplayFont, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Text });
        nav.Children.Add(brand);
        foreach (var (page, glyph, label, element) in new (AppPage, string, string, FrameworkElement)[]
        {
            (AppPage.Home, "\uE80F", "Home", Home),
            (AppPage.History, "\uE81C", "History", History),
            (AppPage.Settings, "\uE713", "Settings", Settings),
            (AppPage.Help, "\uE897", "Help", Help),
        })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Theme.Icon(glyph, 16));
            content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var button = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 14,
                Foreground = Theme.Text,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            Theme.Animate(button, NavRest, Theme.Color("#E2E2E8"), Theme.Color("#D8D8E0"));
            button.Click += (_, _) => ShowPage(page);
            nav.Children.Add(button);
            _pages[page] = (button, element);
        }

        var footer = new StackPanel { Margin = new Thickness(20, 0, 16, 18), VerticalAlignment = VerticalAlignment.Bottom };
        var privacy = new StackPanel { Orientation = Orientation.Horizontal };
        privacy.Children.Add(Theme.Icon("\uE72E", 13, Theme.Muted));
        privacy.Children.Add(new TextBlock { Text = "Offline and private", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.Muted, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        footer.Children.Add(privacy);
        var footnote = Theme.Label("Speech is turned into text on this PC. Nothing you say is uploaded.", 12, Theme.Muted);
        footnote.Margin = new Thickness(0, 4, 0, 10);
        footer.Children.Add(footnote);
        footer.Children.Add(Theme.Label($"Version {services.AppVersion}", 11, Theme.Muted));

        var sidebarContent = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        sidebarContent.Children.Add(footer);
        sidebarContent.Children.Add(nav);

        var layout = new DockPanel();
        var sidebar = new Border { Background = Theme.Sidebar, Width = 236, Child = sidebarContent };
        DockPanel.SetDock(sidebar, Dock.Left);
        layout.Children.Add(sidebar);
        layout.Children.Add(_content);
        Content = layout;
        Background = Theme.Background;
        ShowPage(AppPage.Home);
    }

    public HomePage Home { get; }

    public HistoryPage History { get; }

    public SettingsPage Settings { get; }

    public HelpPage Help { get; }

    public AppPage Page { get; private set; }

    public void ShowPage(AppPage page)
    {
        Page = page;
        foreach (var (key, (button, _)) in _pages)
        {
            Theme.SetRestColor(button, key == page ? NavSelected : NavRest);
            button.FontWeight = key == page ? FontWeights.SemiBold : FontWeights.Normal;
        }

        var element = _pages[page].Page;
        if (!ReferenceEquals(_content.Content, element))
        {
            _content.Content = element;
            Motion.FadeIn(element, 180, rise: 10);
        }
    }
}
