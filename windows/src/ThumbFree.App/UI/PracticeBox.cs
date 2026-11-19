using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ThumbFree.App.UI;

/// <summary>
/// A text box in the first-run guide that takes dictation directly (ThumbFree never types into its own windows). While
/// it has the keyboard focus it's <see cref="PracticeHost.Active"/>; the host sends it the live words and the finished
/// text, and it lays the text out like a real email or notes box would (<see cref="AppServices.Layout"/>).
/// </summary>
internal sealed class PracticeBox : IPracticeTarget
{
    private readonly AppServices _services;
    private readonly TextBox _box;
    private readonly TextBlock _live = Theme.Label(string.Empty, 12, Theme.Muted);
    private readonly bool _email;
    private readonly Action _changed;
    private readonly Dispatcher _dispatcher;
    private volatile bool _empty = true;

    /// <param name="email">An email body: emails are laid out even without a signed name.</param>
    /// <param name="changed">Called on the UI thread when dictated text arrives or the box is edited.</param>
    public PracticeBox(AppServices services, bool email, string placeholder, int lines, Action changed)
    {
        _services = services;
        _email = email;
        _changed = changed;
        _box = Theme.TextBox(multiLine: true, minHeight: (lines * 19) + 14);
        _box.FontSize = 14;
        System.Windows.Automation.AutomationProperties.SetName(_box, placeholder);
        _dispatcher = _box.Dispatcher;
        _box.TextChanged += (_, _) =>
        {
            _empty = _box.Text.Trim().Length == 0;
            _changed();
        };
        _box.GotKeyboardFocus += (_, _) => _services.Practice.SetActive(this);
        _box.LostKeyboardFocus += (_, _) => Release();
        _live.FontStyle = FontStyles.Italic;
        _live.Margin = new Thickness(2, 6, 0, 0);
        _live.Visibility = Visibility.Collapsed;
        var panel = new StackPanel();
        panel.Children.Add(Theme.WithPlaceholder(_box, placeholder));
        panel.Children.Add(_live);
        View = panel;
    }

    public FrameworkElement View { get; }

    public string Text => _box.Text;

    public PracticeField Field => new(MultiLine: true, AtStart: _empty, Email: _email);

    public void ShowLive(string? text) => _dispatcher.BeginInvoke(() =>
    {
        _live.Text = string.IsNullOrWhiteSpace(text) ? string.Empty : "Hearing: " + text;
        _live.Visibility = _live.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    });

    public void Insert(string text) => _dispatcher.BeginInvoke(() =>
    {
        _live.Text = string.Empty;
        _live.Visibility = Visibility.Collapsed;
        InsertNow(text);
    });

    /// <summary>Shows a finished result without dictating (offscreen renders of the guide).</summary>
    internal void Simulate(string result) => _box.Text = result;

    public void Focus() => _box.Focus();

    /// <summary>Stops taking dictation (the step changed or the guide closed).</summary>
    public void Release()
    {
        if (ReferenceEquals(_services.Practice.Active, this))
        {
            _services.Practice.SetActive(null);
        }
    }

    private void InsertNow(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var field = Field;
        if (_services.Current.SmartFormatting && _services.Layout is { } layout)
        {
            text = layout(text, field);
        }

        var caret = _box.CaretIndex;
        var before = _box.Text[..caret];
        var space = before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? (text.Contains('\n') ? "\n" : " ") : string.Empty;
        _box.SelectedText = space + text;
        _box.CaretIndex = caret + space.Length + text.Length;
        _box.SelectionLength = 0;
    }
}
