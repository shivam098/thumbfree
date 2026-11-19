using ThumbFree.Core;

namespace ThumbFree.App;

/// <summary>
/// Speech models on this PC, as the app window and the guide see them. The model store implements it (downloads
/// pinned by SHA-256 into the models folder); pages never call it directly but go through <see cref="ModelDownloads"/>.
/// </summary>
internal interface IModelManager
{
    /// <summary>Raised (any thread) when a model appears or goes other than through this interface, e.g. the host downloaded it.</summary>
    event Action<ModelSpec>? Changed;

    /// <summary>Every file of the model is on this PC, complete.</summary>
    bool IsInstalled(ModelSpec spec);

    /// <summary>
    /// Downloads the model's missing files, reporting progress from 0 to 1, and resumes partial files. Throws
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled (any other
    /// cancellation, such as HttpClient's timeout, is shown as a stalled download); on failure, an exception whose
    /// message can be shown as is (for example "Not enough free space: 661 MB needed.").
    /// </summary>
    Task DownloadAsync(ModelSpec spec, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Deletes the model's files (never a copy that Voice Assistant or Voice Dictation owns).</summary>
    void Delete(ModelSpec spec);
}

internal enum ModelStatus
{
    NotInstalled,
    Downloading,
    Installed,
}

/// <summary>A model's state for the UI: <paramref name="Progress"/> from 0 to 1 while downloading; the last error, if any.</summary>
internal readonly record struct ModelState(ModelStatus Status, double Progress = 0, string? Error = null);

/// <summary>
/// Runs model downloads for the whole app: one per model, cancellable, with progress that Home, Settings and the guide
/// all show (pages come and go; downloads keep going). The dictation host should start its downloads here too
/// (<see cref="Start"/>), so the window shows them. <see cref="Changed"/> is raised on the UI thread.
/// </summary>
internal sealed class ModelDownloads
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Running> _running = [];
    private readonly Dictionary<string, string> _errors = [];
    private readonly SynchronizationContext? _context;

    public ModelDownloads(IModelManager models)
    {
        Models = models;
        _context = SynchronizationContext.Current;
        models.Changed += spec =>
        {
            lock (_gate)
            {
                _errors.Remove(spec.Id);
            }

            RaiseChanged(spec);
        };
    }

    public event Action<ModelSpec>? Changed;

    public IModelManager Models { get; }

    public ModelState State(ModelSpec spec)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(spec.Id, out var running))
            {
                return new ModelState(ModelStatus.Downloading, running.Progress);
            }

            if (Models.IsInstalled(spec))
            {
                return new ModelState(ModelStatus.Installed, 1);
            }

            return new ModelState(ModelStatus.NotInstalled, 0, _errors.GetValueOrDefault(spec.Id));
        }
    }

    /// <summary>Starts downloading (or joins the download already running). The task never throws; it ends when the download does.</summary>
    public Task Start(ModelSpec spec)
    {
        Running running;
        lock (_gate)
        {
            if (_running.TryGetValue(spec.Id, out var existing))
            {
                return existing.Task;
            }

            if (Models.IsInstalled(spec))
            {
                return Task.CompletedTask;
            }

            _errors.Remove(spec.Id);
            running = new Running(new CancellationTokenSource());
            _running[spec.Id] = running;
            running.Task = Task.Run(() => RunAsync(spec, running));
        }

        RaiseChanged(spec);
        return running.Task;
    }

    public void Cancel(ModelSpec spec)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(spec.Id, out var running))
            {
                running.Cancel.Cancel();
            }
        }
    }

    /// <summary>Stops any download of the model and deletes its files.</summary>
    public void Delete(ModelSpec spec)
    {
        Task? stopping = null;
        lock (_gate)
        {
            if (_running.TryGetValue(spec.Id, out var running))
            {
                running.Cancel.Cancel();
                stopping = running.Task;
            }

            _errors.Remove(spec.Id);
        }

        if (stopping is null)
        {
            DeleteNow(spec);
        }
        else
        {
            _ = stopping.ContinueWith(_ => DeleteNow(spec), TaskScheduler.Default);
        }
    }

    private void DeleteNow(ModelSpec spec)
    {
        try
        {
            Models.Delete(spec);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"model delete failed: {ex.GetType().Name}");
            lock (_gate)
            {
                _errors[spec.Id] = "Couldn't delete it: it may be in use. Try again after dictating.";
            }
        }

        RaiseChanged(spec);
    }

    private async Task RunAsync(ModelSpec spec, Running running)
    {
        var lastShown = -1;
        var progress = new Progress(value =>
        {
            running.Progress = Math.Clamp(value, 0, 1);
            var shown = (int)(running.Progress * 200);
            if (shown != lastShown)
            {
                lastShown = shown;
                RaiseChanged(spec);
            }
        });
        string? error = null;
        try
        {
            await Models.DownloadAsync(spec, progress, running.Cancel.Token).ConfigureAwait(false);
            Log.Info($"model {spec.Id} downloaded");
        }
        catch (OperationCanceledException) when (running.Cancel.IsCancellationRequested)
        {
            Log.Info($"model {spec.Id} download cancelled");
        }
        catch (Exception ex)
        {
            Log.Warn($"model {spec.Id} download failed: {ex.GetType().Name}");
            error = ex switch
            {
                // Not the person's Cancel: HttpClient's timeout, or a stream that stopped sending.
                OperationCanceledException => "The download stalled. Try again.",
                HttpRequestException => "Couldn't download it. Check the internet connection, then try again.",
                InvalidDataException => "The download was damaged. Try again.",
                _ => ex.Message,
            };
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(spec.Id);
                if (error is not null)
                {
                    _errors[spec.Id] = error;
                }
            }

            running.Cancel.Dispose();
            RaiseChanged(spec);
        }
    }

    private void RaiseChanged(ModelSpec spec)
    {
        if (Changed is not { } handler)
        {
            return;
        }

        if (_context is null)
        {
            handler(spec);
        }
        else
        {
            _context.Post(_ => handler(spec), null);
        }
    }

    private sealed class Running(CancellationTokenSource cancel)
    {
        public CancellationTokenSource Cancel { get; } = cancel;

        public Task Task { get; set; } = Task.CompletedTask;

        public double Progress { get; set; }
    }

    /// <summary>Reports on the calling thread (IProgress&lt;T&gt; from <see cref="System.Progress{T}"/> would post to the UI thread).</summary>
    private sealed class Progress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

/// <summary>
/// Stand-in until the model store is plugged in: a model counts as installed when all its files are in the models
/// folder (or Voice Assistant's or Voice Dictation's); downloading isn't available yet.
/// </summary>
internal sealed class OfflineModelManager : IModelManager
{
    public event Action<ModelSpec>? Changed;

    public bool IsInstalled(ModelSpec spec) =>
        spec.Files.All(f => AppPaths.FindModel(f.Path.Replace('/', Path.DirectorySeparatorChar)) is { } path && new FileInfo(path).Length == f.Bytes);

    public Task DownloadAsync(ModelSpec spec, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("Downloading speech models comes with the next update of ThumbFree."));

    public void Delete(ModelSpec spec)
    {
        foreach (var file in spec.Files)
        {
            var path = Path.Combine(AppPaths.ModelsDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        Changed?.Invoke(spec);
    }
}

/// <summary>
/// Big models (over 300 MB) ask before downloading (PLAN 3.1): their size, this PC's memory against what the model
/// needs, and a warning on a metered connection.
/// </summary>
internal static class ModelConsent
{
    public const long BigModelBytes = 300_000_000;

    public static bool Needed(ModelSpec spec) => spec.Bytes > BigModelBytes;

    /// <summary>"English, accurate" from "English, accurate (slower; needs 6 GB+ memory)".</summary>
    public static string ShortName(ModelSpec spec)
    {
        var bracket = spec.Label.IndexOf(" (", StringComparison.Ordinal);
        return bracket > 0 ? spec.Label[..bracket] : spec.Label;
    }

    public static string Title(ModelSpec spec) => $"Download “{ShortName(spec)}”?";

    public static string Message(ModelSpec spec, long totalMemoryBytes, bool metered)
    {
        var gb = (int)Math.Round(totalMemoryBytes / (double)(1L << 30));
        var text = $"This model is {Mb(spec.Bytes)}. It makes the fewest mistakes, but it's slower and needs about " +
            $"{Mb(spec.Bytes * 3 / 2)} of free memory while you dictate.\n\nThis PC has {gb} GB of memory.";
        text += gb < spec.RecommendedMemoryGb
            ? $" That may not be enough ({spec.RecommendedMemoryGb} GB or more is best): dictation could be slow, and other apps may slow down while it works."
            : " That's enough.";
        if (metered)
        {
            text += "\n\nYou're on a metered connection, so this may use up your data allowance. Wi-Fi or an unmetered connection is better.";
        }

        return text;
    }

    /// <summary>Sizes the way the Android app shows them: decimal megabytes, rounded.</summary>
    public static string Mb(long bytes) => $"{(bytes + 500_000) / 1_000_000} MB";
}
