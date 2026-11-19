using System.Drawing;
using System.Windows.Forms;
using ThumbFree.App.Platform;
using ThumbFree.Core;

namespace ThumbFree.App.UI;

/// <summary>What the tray menu asks of the app: open a page of the window, paste the last dictation, exit.</summary>
internal sealed record TrayCommands(Action<AppPage> Open, Action PasteLast, Action Exit);

/// <summary>
/// The tray icon: a status dot on the logo, the menu (<see cref="TrayMenu"/>), and the balloon that Windows shows as a
/// notification (<see cref="INotificationSurface"/>).
/// </summary>
internal sealed class TrayIcon : IDisposable, INotificationSurface
{
    private readonly AppServices _services;
    private readonly NotifyIcon _icon;
    private readonly TrayMenu _menu;
    private readonly (Icon Icon, nint Handle) _readyIcon;
    private readonly (Icon Icon, nint Handle) _recordingIcon;
    private readonly (Icon Icon, nint Handle) _pausedIcon;
    private object? _balloonTag;
    private bool _paused;
    private bool _recording;

    public TrayIcon(AppServices services, OverlayWindow overlay, TrayCommands commands)
    {
        _services = services;
        _readyIcon = MakeIcon(null);
        _recordingIcon = MakeIcon(Color.FromArgb(229, 72, 77));
        _pausedIcon = MakeIcon(Color.FromArgb(154, 160, 166));
        _menu = new TrayMenu(services, commands, () => overlay.Pinned, () => overlay.Pinned = !overlay.Pinned, () => _paused);
        _menu.Strip.Opening += (_, _) => Refresh();

        _icon = new NotifyIcon
        {
            Icon = _readyIcon.Icon,
            Text = "ThumbFree",
            ContextMenuStrip = _menu.Strip,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                commands.Open(AppPage.Home);
            }
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonTag is AppPage page)
            {
                commands.Open(page);
            }

            Clicked?.Invoke(_balloonTag);
        };
        services.State.Changed += Refresh;
        services.Models.Changed += _ => Refresh();
        services.Settings.Changed += _ => Refresh();
        Refresh();
    }

    public event Action<object?>? Clicked;

    public void SetRecording(bool recording)
    {
        _recording = recording;
        Refresh();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        Refresh();
    }

    public void ShowBalloon(string message, bool warning = true) => Show("ThumbFree", message, warning, null);

    public void Show(string title, string text, bool warning, object? tag)
    {
        _balloonTag = tag;
        _icon.ShowBalloonTip(5000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// <summary>Removing and re-adding the icon is the only way to take a balloon back (it also leaves the notification centre).</summary>
    public void Hide()
    {
        _balloonTag = null;
        _icon.Visible = false;
        _icon.Visible = true;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        foreach (var (icon, handle) in new[] { _readyIcon, _recordingIcon, _pausedIcon })
        {
            icon.Dispose();
            Win32.DestroyIcon(handle);
        }
    }

    private void Refresh()
    {
        var model = _services.Models.State(_services.Current.SelectedModel());
        var (icon, label) = _recording ? (_recordingIcon.Icon, "Recording — tap Ctrl+Win to finish")
            : _paused ? (_pausedIcon.Icon, "Paused (dictation keys off)")
            : model.Status == ModelStatus.Downloading ? (_pausedIcon.Icon, $"Downloading the speech model… {(int)(model.Progress * 100)}%")
            : model.Status == ModelStatus.NotInstalled ? (_pausedIcon.Icon, "Speech model needed — open ThumbFree")
            : (_readyIcon.Icon, "Ready — tap Ctrl+Win to dictate");
        _icon.Icon = icon;
        _icon.Text = $"ThumbFree — {label}";
        _menu.ShowStatus(label, _paused);
    }

    /// <summary>The app logo with a status dot in the corner (no dot when ready).</summary>
    private static (Icon, nint) MakeIcon(Color? dot)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/ThumbFree;component/Assets/app.ico")).Stream;
        using var logo = new Icon(stream, 32, 32);
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.DrawIcon(logo, new Rectangle(0, 0, 32, 32));
            if (dot is { } color)
            {
                using var ring = new SolidBrush(Color.White);
                g.FillEllipse(ring, 17, 17, 15, 15);
                using var fill = new SolidBrush(color);
                g.FillEllipse(fill, 19, 19, 11, 11);
            }
        }

        var handle = bitmap.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }
}

/// <summary>
/// The tray menu: open ThumbFree, the status, the last five dictations (click copies), History, the text that wasn't
/// typed, paste last, language, pause, Settings, Help, the guide, the pill, and Exit. Built apart from the icon so tests
/// can draw it.
/// </summary>
internal sealed class TrayMenu : IDisposable
{
    private const int RecentCount = 5;
    private const int RecentChars = 52;

    private readonly AppServices _services;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _pause;
    private readonly ToolStripMenuItem _untyped;
    private readonly ToolStripMenuItem _english;
    private readonly ToolStripMenuItem _hinglish;
    private readonly ToolStripMenuItem _showPill;
    private readonly ToolStripSeparator _recentEnd = new();
    private readonly List<ToolStripItem> _recent = [];
    private readonly Func<bool> _pillPinned;

    /// <param name="paused">Whether the dictation keys are paused now (the pause item flips it).</param>
    public TrayMenu(AppServices services, TrayCommands commands, Func<bool> pillPinned, Action togglePill, Func<bool> paused)
    {
        _services = services;
        _pillPinned = pillPinned;
        var settings = services.Current;
        var open = new ToolStripMenuItem("Open ThumbFree", null, (_, _) => commands.Open(AppPage.Home));
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        _status = new ToolStripMenuItem("Starting…") { Enabled = false };
        _pause = new ToolStripMenuItem("Pause dictation keys", null, (_, _) => services.SetPaused(!paused()));
        _untyped = new ToolStripMenuItem("Copy the text that wasn't typed", null, (_, _) => services.Notifier?.CopyUntyped());
        _english = new ToolStripMenuItem("English", null, (_, _) => services.Settings.Update(s => s.Language = Language.English));
        _hinglish = new ToolStripMenuItem("Hindi + English (typed in Roman letters)", null, (_, _) => services.Settings.Update(s => s.Language = Language.Hinglish));
        var language = new ToolStripMenuItem("Dictation language") { ShortcutKeyDisplayString = settings.DictationLanguageHotkey };
        language.DropDownItems.AddRange([_english, _hinglish]);
        _showPill = new ToolStripMenuItem("Always show status pill", null, (_, _) => togglePill());

        Strip.Items.AddRange(
        [
            open,
            new ToolStripSeparator(),
            _status,
            _recentEnd,
            new ToolStripMenuItem("History…", null, (_, _) => commands.Open(AppPage.History)),
            _untyped,
            new ToolStripMenuItem("Paste last dictation", null, (_, _) => commands.PasteLast()) { ShortcutKeyDisplayString = settings.PasteLastHotkey },
            language,
            _pause,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Settings", null, (_, _) => commands.Open(AppPage.Settings)),
            new ToolStripMenuItem("Help", null, (_, _) => commands.Open(AppPage.Help)),
            new ToolStripMenuItem("Show the guide again", null, (_, _) => services.ShowGuide()),
            _showPill,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => commands.Exit()),
        ]);
        Strip.Opening += (_, _) => Prepare();
    }

    public ContextMenuStrip Strip { get; } = new() { ShowItemToolTips = true };

    /// <summary>Brings the menu up to date before it opens: recent dictations, checks, kept text.</summary>
    public void Prepare()
    {
        _showPill.Checked = _pillPinned();
        _english.Checked = _services.Current.Language == Language.English;
        _hinglish.Checked = _services.Current.Language == Language.Hinglish;
        _untyped.Visible = _services.Notifier?.UntypedText is not null;
        ShowRecent();
    }

    public void ShowStatus(string label, bool paused)
    {
        _status.Text = label;
        _pause.Text = paused ? "Resume dictation keys" : "Pause dictation keys";
    }

    public void Dispose() => Strip.Dispose();

    /// <summary>One line, at most <see cref="RecentChars"/> characters, safe for a menu ("&amp;" isn't a shortcut).</summary>
    internal static string MenuText(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length > RecentChars)
        {
            var cut = line.LastIndexOf(' ', RecentChars - 1);
            line = line[..(cut > RecentChars / 2 ? cut : RecentChars - 1)].TrimEnd(',', '.', ';', ':') + "…";
        }

        return line.Replace("&", "&&", StringComparison.Ordinal);
    }

    /// <summary>The last dictations, newest first, right in the menu: click one to copy it.</summary>
    private void ShowRecent()
    {
        foreach (var item in _recent)
        {
            Strip.Items.Remove(item);
            item.Dispose();
        }

        _recent.Clear();
        var entries = _services.Current.KeepHistory ? _services.History.Entries().Take(RecentCount).ToList() : [];
        if (entries.Count > 0)
        {
            _recent.Add(new ToolStripSeparator());
            _recent.Add(new ToolStripMenuItem("Recent dictations: click one to copy it") { Enabled = false });
        }

        foreach (var entry in entries)
        {
            _recent.Add(new ToolStripMenuItem(MenuText(entry.Text), null, (_, _) => _services.Environment.CopyText(entry.Text))
            {
                ToolTipText = entry.Text.Length > 300 ? entry.Text[..300] + "…" : entry.Text,
            });
        }

        var at = Strip.Items.IndexOf(_recentEnd);
        foreach (var item in _recent)
        {
            Strip.Items.Insert(at++, item);
        }
    }
}
