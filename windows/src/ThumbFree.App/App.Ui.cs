using System.Windows;
using ThumbFree.App.UI;
using ThumbFree.Core;

namespace ThumbFree.App;

/// <summary>
/// The app window, the first-run guide, the tray and the stores behind them. OnStartup calls these; the dictation host
/// reaches History, samples, notifications, models and the guide's practice boxes through <see cref="_services"/>.
/// </summary>
public partial class App
{
    private const string ShowWindowEvent = @"Local\ThumbFree.ShowWindow";

    private AppServices? _services;
    private MainWindow? _window;
    private GuideWindow? _guide;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private bool _syncingLanguage;

    /// <summary>A launch from the Start menu while ThumbFree runs opens the running copy's window instead.</summary>
    private static bool ShowRunningCopy()
    {
        if (!EventWaitHandle.TryOpenExisting(ShowWindowEvent, out var show))
        {
            return false;
        }

        // This launch came from the person, so it may hand its right to take the foreground to the running copy.
        WindowsEnvironment.AllowAnyForeground();
        show.Set();
        show.Dispose();
        return true;
    }

    private AppServices CreateServices(AppSettings settings)
    {
        // The stores raise their events through the UI thread's context, captured when they're made.
        if (SynchronizationContext.Current is null)
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
        }

        var store = new SettingsStore(settings, AppPaths.SettingsFile);
        var services = new AppServices
        {
            Settings = store,
            History = new HistoryStore(AppPaths.HistoryFile, () => store.Current.KeepHistory),
            Samples = new SampleStore(AppPaths.SamplesDir, () => store.Current.SaveSamples, typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0"),
            Models = new ModelDownloads(new OfflineModelManager()),
            State = new DictationState(),
            Environment = new WindowsEnvironment(),
        };
        services.SetPaused = SetPaused;
        services.ShowGuide = ShowGuide;
        services.OpenPage = OpenPage;
        WindowsEnvironment.UpgradeStartupEntry();
        return services;
    }

    /// <summary>
    /// Feeds the window from the dictation host. The language lives in settings.json now while this host still keeps
    /// it in flag files, so changes are mirrored both ways until the host reads <see cref="AppSettings.Language"/>.
    /// </summary>
    private void WireUi(DictationHost host, AppServices services)
    {
        host.Status += services.State.Report;
        host.Activity += services.State.SetActivity;
        host.DictationLanguageChanged += (hinglish, accurate) =>
        {
            _syncingLanguage = true;
            try
            {
                services.Settings.Update(s =>
                {
                    s.Language = hinglish ? Language.Hinglish : Language.English;
                    s.HinglishTier = accurate ? Tier.Accurate : Tier.Standard;
                });
            }
            finally
            {
                _syncingLanguage = false;
            }
        };
        services.Settings.Changed += change =>
        {
            if (!_syncingLanguage && change.Has(nameof(AppSettings.Language), nameof(AppSettings.HinglishTier)))
            {
                host.SetDictationLanguage(services.Current.Language == Language.Hinglish, services.Current.HinglishTier == Tier.Accurate);
            }
        };

        var hinglish = services.Current.Language == Language.Hinglish;
        if (hinglish != host.HinglishDictation || (hinglish && (services.Current.HinglishTier == Tier.Accurate) != host.HinglishAccurate))
        {
            host.SetDictationLanguage(hinglish, services.Current.HinglishTier == Tier.Accurate);
        }
    }

    /// <summary>
    /// A failure before the tray icon exists shows an error and exits (which releases the single-instance mutex) rather
    /// than leaving a hidden copy running.
    /// </summary>
    private bool FailedBeforeTray(Exception ex) => new StartupGuard(
        message => System.Windows.MessageBox.Show(message, "ThumbFree", MessageBoxButton.OK, MessageBoxImage.Error),
        () => Shutdown(1)).Handle(ex, trayExists: _tray is not null);

    /// <summary>First run opens the guide; any other launch by the person opens the window (sign-in starts quietly in the tray).</summary>
    private void OpenAtStart(string[] args)
    {
        if (_services is null)
        {
            return;
        }

        if (_services.Settings.ProblemSummary is { } problem)
        {
            // Clicking it opens Settings, which says what to fix.
            _tray?.Show("ThumbFree", problem, warning: true, AppPage.Settings);
        }

        if (!_services.Current.GuideDone)
        {
            ShowGuide();
        }
        else if (!args.Contains(WindowsEnvironment.BackgroundArgument))
        {
            OpenPage(AppPage.Home);
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEvent);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(() => OpenPage(AppPage.Home)), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private void OpenPage(AppPage page)
    {
        if (_services is null)
        {
            return;
        }

        _window ??= new MainWindow(_services, message => _tray?.ShowBalloon(message, warning: false));
        _window.ShowPage(page);
        _window.Bring();
    }

    private void ShowGuide()
    {
        if (_services is null)
        {
            return;
        }

        if (_guide is not null)
        {
            _guide.Activate();
            return;
        }

        _guide = new GuideWindow(_services, () => OpenPage(AppPage.Home));
        _guide.Closed += (_, _) => _guide = null;
        _guide.Show();
        _guide.Activate();
    }

    private void Quit()
    {
        _window?.AllowClose();
        Shutdown();
    }

    /// <summary>Lets History and samples finish writing before the process ends.</summary>
    private void CloseUi()
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _services?.History.Flush(TimeSpan.FromSeconds(2));
        _services?.Samples.Flush(TimeSpan.FromSeconds(2));
    }
}
