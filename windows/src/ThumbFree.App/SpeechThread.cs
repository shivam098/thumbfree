using System.Collections.Concurrent;
using System.Diagnostics;
using ThumbFree.Core;
using ThumbFree.Speech;

namespace ThumbFree.App;

/// <summary>
/// All speech recognition runs here, one job at a time on one background thread, as in the Android app's SpeechEngine:
/// the models aren't thread-safe. A model loads when a dictation first needs it and stays loaded until
/// <see cref="UnloadAll"/> (the host calls it after a few idle minutes, PLAN 3.1).
/// </summary>
internal sealed class SpeechThread : IDisposable
{
    private readonly BlockingCollection<Action> _jobs = new();
    private readonly Thread _thread;
    private readonly Dictionary<string, IRecognizer> _loaded = new(StringComparer.Ordinal);
    private int _disposed;

    public SpeechThread(ISpeechModels models)
    {
        Models = models;
        _thread = new Thread(Run) { IsBackground = true, Name = "speech" };
        _thread.Start();
    }

    public ISpeechModels Models { get; }

    /// <summary>Queues a job; it runs after every job queued before it. Its exceptions are logged.</summary>
    public void Post(Action job)
    {
        try
        {
            _jobs.Add(job);
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
        }
    }

    /// <summary>Queues a job and returns its result (cancelled if ThumbFree is shutting down).</summary>
    public Task<T> Run<T>(Func<T> job)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _jobs.Add(() =>
            {
                try
                {
                    done.SetResult(job());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            done.SetCanceled();
        }

        return done.Task;
    }

    /// <summary>
    /// Speech thread: the recognizer for <paramref name="spec"/>, loaded if needed. Other models are unloaded first,
    /// except <paramref name="keep"/>, so at most two are in memory (a model and the one that previews for it).
    /// </summary>
    public IRecognizer Model(ModelSpec spec, int threads, ModelSpec? keep = null)
    {
        if (_loaded.TryGetValue(spec.Id, out var model))
        {
            return model;
        }

        foreach (var id in _loaded.Keys.Where(id => id != keep?.Id).ToList())
        {
            Unload(id);
        }

        var started = Stopwatch.StartNew();
        model = Models.Load(spec, threads);
        _loaded[spec.Id] = model;
        Log.Info($"model {spec.Id} loaded in {started.ElapsedMilliseconds} ms ({threads} threads)");
        return model;
    }

    /// <summary>Speech thread: frees every model.</summary>
    public void UnloadAll()
    {
        foreach (var id in _loaded.Keys.ToList())
        {
            Unload(id);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _jobs.CompleteAdding();
        if (_thread.Join(TimeSpan.FromSeconds(5)))
        {
            UnloadAll();
            Models.Dispose();
            _jobs.Dispose();
        }
        else
        {
            // Freeing a model in the middle of a transcription crashes; Windows frees it when ThumbFree exits.
            Log.Warn("speech thread still busy at exit; leaving the models to process exit");
        }
    }

    private void Unload(string id)
    {
        _loaded.Remove(id, out var model);
        model?.Dispose();
        Log.Info($"model {id} unloaded");
    }

    private void Run()
    {
        foreach (var job in _jobs.GetConsumingEnumerable())
        {
            try
            {
                job();
            }
            catch (Exception ex)
            {
                Log.Error($"speech job failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            }
        }
    }
}
