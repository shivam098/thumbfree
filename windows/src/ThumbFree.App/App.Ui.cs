using System.Windows;
using ThumbFree.App.UI;
using ThumbFree.Core;
using ThumbFree.Speech;

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

    private AppServices CreateServices(AppSettings settings, ModelStore models)
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
            Models = new ModelDownloads(new StoreModelManager(models)),
            State = new DictationState(),
            Environment = new WindowsEnvironment(),
            Layout = (text, field) => Layout.Apply(text, new LayoutContext(field.MultiLine, field.AtStart, field.Email)),
        };
        services.SetPaused = SetPaused;
        services.ShowGuide = ShowGuide;
        services.OpenPage = OpenPage;
        WindowsEnvironment.UpgradeStartupEntry();
        return services;
    }

    /// <summary>Feeds the window and the pill from the dictation host and the settings.</summary>
    private void WireUi(DictationHost host, AppServices services)
    {
        host.Status += services.State.Report;
        host.Activity += services.State.SetActivity;
        services.Settings.Changed += change =>
        {
            if (change.Has(nameof(AppSettings.Language), nameof(AppSettings.HinglishTier)))
            {
                ShowLanguage(services.Current);
            }
        };
        ShowLanguage(services.Current);
    }

    /// <summary>
    /// A failure before the tray icon exists shows an error and exits (which releases the single-instance mutex) rather
    /// than leaving a hidden copy running.
    /// </summary>
    private bool FailedBeforeTray(Exception ex) => new StartupGuard(
        message => System.Windows.MessageBox.Show(message, "ThumbFree", MessageBoxButton.OK, MessageBoxImage.Error),
        () => Shutdown(1)).Handle(ex, trayExists: _tray is not null);

    /// <summary>The pill's language badge.</summary>
    private void ShowLanguage(AppSettings settings) =>
        _overlay?.SetDictationLanguage(settings.Language == Language.Hinglish, settings.HinglishTier == Tier.Accurate);

    /// <summary>Alt+Shift+H: English ⇄ Hindi + English, saved in Settings; the tier chosen for each language stays.</summary>
    private void ToggleLanguage() =>
        _services?.Settings.Update(s => s.Language = s.Language == Language.Hinglish ? Language.English : Language.Hinglish);

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
