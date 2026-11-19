using System.Windows;
using ThumbFree.Core;

namespace ThumbFree.App.Tests;

/// <summary>A temporary folder per test, deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "thumbfree-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Windows as tests see it: nothing touches the clipboard, the registry or the screen; answers are scripted.</summary>
internal sealed class FakeEnvironment : IAppEnvironment
{
    public List<string> Copied { get; } = [];

    public List<string> Opened { get; } = [];

    public List<Confirmation> Questions { get; } = [];

    public bool Answer { get; set; } = true;

    public string? SavePath { get; set; }

    public bool StartAtSignIn { get; set; }

    public long TotalMemoryBytes { get; set; } = 16L << 30;

    public bool Metered { get; set; }

    public MicrophoneAccess Microphone { get; set; } = MicrophoneAccess.Allowed;

    public bool Notifications { get; set; } = true;

    public void CopyText(string text) => Copied.Add(text);

    public void Open(string target, string? arguments = null) => Opened.Add(target);

    public void ShowInFolder(string path) => Opened.Add(path);

    public bool IsMeteredConnection() => Metered;

    public MicrophoneAccess CheckMicrophone() => Microphone;

    public bool NotificationsEnabled() => Notifications;

    public bool Confirm(Window? owner, Confirmation question)
    {
        Questions.Add(question);
        return Answer;
    }

    public string? AskSavePath(Window? owner, string title, string fileName, string filter) => SavePath;
}

/// <summary>Models that are "installed" when listed; downloads finish when told to, or fail with <see cref="Failure"/>.</summary>
internal sealed class FakeModelManager : IModelManager
{
    private readonly Dictionary<string, TaskCompletionSource> _downloads = [];

    public event Action<ModelSpec>? Changed;

    public HashSet<string> Installed { get; } = [];

    public Dictionary<string, IProgress<double>> Progress { get; } = [];

    public Exception? Failure { get; set; }

    public bool IsInstalled(ModelSpec spec) => Installed.Contains(spec.Id);

    public async Task DownloadAsync(ModelSpec spec, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_downloads)
        {
            _downloads[spec.Id] = done;
            if (progress is not null)
            {
                Progress[spec.Id] = progress;
            }
        }

        using (cancellationToken.Register(() => done.TrySetCanceled(cancellationToken)))
        {
            await done.Task;
        }

        Installed.Add(spec.Id);
    }

    /// <summary>Waits until the download has started (it runs on the thread pool).</summary>
    public void WaitStarted(ModelSpec spec)
    {
        for (var i = 0; i < 500; i++)
        {
            lock (_downloads)
            {
                if (_downloads.ContainsKey(spec.Id))
                {
                    return;
                }
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("The download never started.");
    }

    public void Report(ModelSpec spec, double value)
    {
        WaitStarted(spec);
        lock (_downloads)
        {
            Progress[spec.Id].Report(value);
        }
    }

    public void Finish(ModelSpec spec)
    {
        WaitStarted(spec);
        lock (_downloads)
        {
            _downloads[spec.Id].TrySetResult();
        }
    }

    public void Delete(ModelSpec spec)
    {
        Installed.Remove(spec.Id);
        Changed?.Invoke(spec);
    }
}

/// <summary>Notifications that are recorded instead of shown.</summary>
internal sealed class FakeSurface : INotificationSurface
{
    public event Action<object?>? Clicked;

    public List<(string Title, string Text, bool Warning, object? Tag)> Shown { get; } = [];

    public int Hidden { get; private set; }

    public void Show(string title, string text, bool warning, object? tag) => Shown.Add((title, text, warning, tag));

    public void Hide() => Hidden++;

    public void Click() => Clicked?.Invoke(Shown[^1].Tag);
}

/// <summary>Builds <see cref="AppServices"/> on temporary files with fakes.</summary>
internal static class TestServices
{
    public static AppServices Create(TempFolder folder, FakeEnvironment? environment = null, IModelManager? models = null, AppSettings? settings = null, Func<DateTimeOffset>? clock = null)
    {
        var store = new SettingsStore(settings ?? new AppSettings(), folder.File("settings.json"));
        return new AppServices
        {
            Settings = store,
            History = new HistoryStore(folder.File("history.txt"), () => store.Current.KeepHistory, clock),
            Samples = new SampleStore(folder.File("samples"), () => store.Current.SaveSamples, "0.8.0", clock),
            Models = new ModelDownloads(models ?? new FakeModelManager()),
            State = new DictationState(),
            Environment = environment ?? new FakeEnvironment(),
            AppVersion = "0.8.0",
        };
    }
}
