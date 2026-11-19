using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ThumbFree.Core;

namespace ThumbFree.App.UI;

/// <summary>
/// History (PLAN 3.11): recent dictations, newest first. Click one to copy it; right-click (or the menu key) for Copy
/// and Delete. A search box appears once there are more than three.
/// </summary>
internal sealed class HistoryPage : UserControl
{
    private const int SearchFrom = 4;

    private readonly AppServices _services;
    private readonly StackPanel _list = new();
    private readonly TextBlock _note = Theme.Label(string.Empty, 13, Theme.Muted);
    private readonly Button _turnOn;
    private readonly TextBox _search = Theme.TextBox();
    private readonly FrameworkElement _searchBox;
    private readonly Button _clear;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly List<(TextBlock Caption, HistoryEntry Entry)> _captions = [];
    private bool _stale = true;

    public HistoryPage(AppServices services)
    {
        _services = services;
        var top = new StackPanel();
        var header = new DockPanel();
        _clear = Theme.Button("Clear history", (_, _) => ClearAll(), danger: true);
        _clear.Margin = new Thickness(16, 0, 0, 0);
        DockPanel.SetDock(_clear, Dock.Right);
        header.Children.Add(_clear);
        header.Children.Add(Theme.Heading("History"));
        top.Children.Add(header);
        var about = Theme.Label("Your dictations from the last 30 days, kept on this PC only. Click one to copy it; right-click for more.", 13, Theme.Muted);
        about.Margin = new Thickness(0, 0, 0, 14);
        top.Children.Add(about);
        _search.Width = 360;
        _search.HorizontalAlignment = HorizontalAlignment.Left;
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search history");
        _searchBox = Theme.WithPlaceholder(_search, "Search");
        _searchBox.HorizontalAlignment = HorizontalAlignment.Left;
        _searchBox.Margin = new Thickness(0, 0, 0, 14);
        top.Children.Add(_searchBox);
        _search.TextChanged += (_, _) => Render();

        var body = new StackPanel();
        var notePanel = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };
        notePanel.Children.Add(_note);
        _turnOn = Theme.Link("Turn history on", (_, _) => _services.Settings.Update(s => s.KeepHistory = true));
        _turnOn.HorizontalAlignment = HorizontalAlignment.Left;
        _turnOn.Margin = new Thickness(-6, 6, 0, 0);
        notePanel.Children.Add(_turnOn);
        body.Children.Add(notePanel);
        body.Children.Add(_list);

        var scroll = new ScrollViewer { Content = Theme.Column(body, 780, new Thickness(32, 0, 32, 24)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Motion.SmoothScroll(scroll);
        var root = new DockPanel();
        var heading = Theme.Column(top, 780, new Thickness(32, 28, 32, 0));
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        root.Children.Add(scroll);
        Content = root;
        Background = Theme.Background;

        services.History.Changed += Invalidate;
        services.Settings.Changed += change =>
        {
            if (change.Has(nameof(AppSettings.KeepHistory)))
            {
                Invalidate();
            }
        };
        _clock.Tick += (_, _) =>
        {
            var now = DateTimeOffset.Now;
            foreach (var (caption, entry) in _captions)
            {
                caption.Text = RelativeTime.Caption(entry, now);
            }
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _clock.Start();
                if (_stale)
                {
                    Render();
                }
            }
            else
            {
                _clock.Stop();
            }
        };
        Render();
    }

    /// <summary>Redraws now if the page is on screen, else when it's next shown.</summary>
    private void Invalidate()
    {
        _stale = true;
        if (IsVisible)
        {
            Render();
        }
    }

    internal void Render()
    {
        _stale = false;
        var all = _services.History.Entries();
        var query = all.Count >= SearchFrom ? _search.Text : string.Empty;
        var shown = History.Search(all, query);
        var now = DateTimeOffset.Now;
        _list.Children.Clear();
        _captions.Clear();
        foreach (var entry in shown)
        {
            _list.Children.Add(Card(entry, now));
        }

        var keep = _services.Current.KeepHistory;
        _note.Text = !keep ? "History is off, so new dictations aren't kept."
            : all.Count == 0 ? "Nothing yet. Your dictations will appear here: click in any text box, tap Ctrl+Win and start talking."
            : shown.Count == 0 ? $"No dictation contains “{query.Trim()}”."
            : string.Empty;
        _note.Visibility = _note.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _turnOn.Visibility = keep ? Visibility.Collapsed : Visibility.Visible;
        _searchBox.Visibility = all.Count >= SearchFrom ? Visibility.Visible : Visibility.Collapsed;
        _clear.Visibility = all.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>One dictation: up to 6 lines of its text and "Outlook · 2 minutes ago". A button, so the keyboard can reach it.</summary>
    private Button Card(HistoryEntry entry, DateTimeOffset now)
    {
        var text = new TextBlock
        {
            Text = entry.Text,
            FontSize = 14,
            Foreground = Theme.Text,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 20,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 120,
        };
        var caption = Theme.Label(RelativeTime.Caption(entry, now), 12, Theme.Muted);
        caption.Margin = new Thickness(0, 8, 0, 0);
        _captions.Add((caption, entry));
        var copied = new TextBlock { Text = "✓ Copied", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Theme.Ok, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var footer = new DockPanel();
        DockPanel.SetDock(copied, Dock.Right);
        footer.Children.Add(copied);
        footer.Children.Add(caption);
        var content = new StackPanel();
        content.Children.Add(text);
        content.Children.Add(footer);

        var card = new Button
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, 10),
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Click to copy",
        };
        System.Windows.Automation.AutomationProperties.SetName(card, $"{entry.Text}. {caption.Text}. Click to copy.");
        Theme.Animate(card, Colors.White, Theme.Color("#F6F5FE"), Theme.Color("#ECEAFD"));

        void Copy()
        {
            _services.Environment.CopyText(entry.Text);
            copied.Visibility = Visibility.Visible;
            Motion.FadeIn(copied, 150);
            var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            hide.Tick += (_, _) =>
            {
                hide.Stop();
                copied.Visibility = Visibility.Collapsed;
            };
            hide.Start();
        }

        card.Click += (_, _) => Copy();
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => Copy();
        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => _services.History.Delete(entry);
        menu.Items.Add(copy);
        menu.Items.Add(delete);
        card.ContextMenu = menu;
        return card;
    }

    private void ClearAll()
    {
        if (_services.Environment.Confirm(Window.GetWindow(this), new Confirmation(
            "Clear history?",
            "Every dictation in History is deleted from this PC. This can't be undone.",
            "Delete all",
            Danger: true)))
        {
            _services.History.Clear();
        }
    }
}
