using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThumbFree.App.UI;

/// <summary>Help: how to dictate, spoken punctuation, smart formatting with examples, Hindi + English, privacy and licences. Searchable.</summary>
internal sealed class HelpPage : UserControl
{
    private readonly AppServices _services;
    private readonly StackPanel _sections = new();
    private readonly TextBox _search = Theme.TextBox();

    public HelpPage(AppServices services)
    {
        _services = services;
        var top = new StackPanel();
        top.Children.Add(Theme.Heading("Help"));
        var about = Theme.Label("How to dictate, what you can say, and how ThumbFree keeps your words on this PC.", 13, Theme.Muted);
        about.Margin = new Thickness(0, 0, 0, 14);
        top.Children.Add(about);
        _search.Width = 360;
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search help");
        var search = Theme.WithPlaceholder(_search, "Search, for example “list” or “Hindi”");
        search.HorizontalAlignment = HorizontalAlignment.Left;
        search.Margin = new Thickness(0, 0, 0, 14);
        top.Children.Add(search);
        _search.TextChanged += (_, _) => Show(_search.Text.Trim());

        var scroll = new ScrollViewer { Content = Theme.Column(_sections, 780, new Thickness(32, 0, 32, 24)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Motion.SmoothScroll(scroll);
        var root = new DockPanel();
        var heading = Theme.Column(top, 780, new Thickness(32, 28, 32, 0));
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        root.Children.Add(scroll);
        Content = root;
        Background = Theme.Background;
        Show(string.Empty);
    }

    private void Show(string filter)
    {
        _sections.Children.Clear();
        foreach (var section in HelpText.Sections(_services.Current))
        {
            bool Matches(string text) => text.Contains(filter, StringComparison.OrdinalIgnoreCase);
            var whole = filter.Length == 0 || Matches(section.Title);
            var lines = whole ? section.Lines : section.Lines.Where(Matches).ToArray();
            var examples = (section.Examples ?? []).Where(e => whole || Matches(e.Said) || Matches(e.Typed)).ToArray();
            if (lines.Length == 0 && examples.Length == 0)
            {
                continue;
            }

            var panel = new StackPanel();
            panel.Children.Add(Theme.Title(section.Title, 16));
            foreach (var line in lines)
            {
                var text = Theme.Label("•  " + line, 13);
                text.Margin = new Thickness(0, 0, 0, 6);
                text.LineHeight = 19;
                panel.Children.Add(text);
            }

            foreach (var (said, typed) in examples)
            {
                panel.Children.Add(Example(said, typed));
            }

            if (section.Title.StartsWith("Licences", StringComparison.Ordinal))
            {
                var notices = Theme.Link("Licence details and sources", (_, _) => _services.Environment.Open(HelpText.NoticesUrl));
                notices.HorizontalAlignment = HorizontalAlignment.Left;
                panel.Children.Add(notices);
            }

            _sections.Children.Add(Theme.CardOf(panel, padding: new Thickness(20, 18, 20, 16)));
        }

        if (_sections.Children.Count == 0)
        {
            _sections.Children.Add(Theme.Label("Nothing matches. Try a single word like “email” or “comma”.", 13, Theme.Muted));
        }
    }

    /// <summary>"You say" beside "ThumbFree types", line breaks and all.</summary>
    private static Grid Example(string said, string typed)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Box("You say", $"“{said}”", Theme.Hex("#F7F7FA"), italic: true));
        var typedBox = Box("ThumbFree types", typed, Theme.Hex("#F5F3FF"), italic: false);
        Grid.SetColumn(typedBox, 2);
        grid.Children.Add(typedBox);
        return grid;
    }

    private static Border Box(string caption, string text, Brush background, bool italic)
    {
        var panel = new StackPanel();
        var label = Theme.Label(caption, 11, Theme.Muted);
        label.FontWeight = FontWeights.SemiBold;
        label.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(label);
        var body = Theme.Label(text, 13);
        body.FontStyle = italic ? FontStyles.Italic : FontStyles.Normal;
        body.LineHeight = 19;
        panel.Children.Add(body);
        return new Border
        {
            Background = background,
            BorderBrush = Theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 10),
            Child = panel,
        };
    }
}
