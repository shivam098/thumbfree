using System.Globalization;
using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using ThumbFree.App.Platform;
using ThumbFree.Core;

namespace ThumbFree.App;

/// <summary>
/// "Help improve ThumbFree" (PLAN 3.8; off unless switched on). Each dictation's audio (a 16 kHz mono WAV) and a note
/// (what was recognised, what was typed, the app's name, the model, the app version and the time) are kept in
/// %LOCALAPPDATA%\ThumbFree\samples: the newest 300, at most 150 MB. Never from password fields or web browsers
/// (<see cref="MayKeep"/>). Nothing is ever sent anywhere: "Save samples as a zip…" makes a file the person shares
/// however they choose, and "Delete all" removes everything. Same folder layout and note fields as the Android app
/// (plus the app, the platform and the time), so the same tools read both.
/// </summary>
internal sealed class SampleStore
{
    public const int MaxSamples = 300;
    public const long MaxBytes = 150_000_000;

    private static readonly JsonSerializerOptions NoteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<bool> _enabled;
    private readonly string _appVersion;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _maxSamples;
    private readonly long _maxBytes;
    private readonly SynchronizationContext? _context;
    private readonly object _gate = new();
    private Task _work = Task.CompletedTask;

    /// <param name="enabled">The "Help improve ThumbFree" switch, read at every <see cref="Save"/>.</param>
    public SampleStore(string folder, Func<bool> enabled, string appVersion, Func<DateTimeOffset>? clock = null, int maxSamples = MaxSamples, long maxBytes = MaxBytes)
    {
        Folder = folder;
        _enabled = enabled;
        _appVersion = appVersion;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _maxSamples = maxSamples;
        _maxBytes = maxBytes;
        _context = SynchronizationContext.Current;
    }

    /// <summary>Raised after samples are added or deleted, on the UI thread when the store was made there.</summary>
    public event Action? Changed;

    public string Folder { get; }

    /// <summary>
    /// The privacy rule for samples (PLAN 3.8): never from a password field, and never from a web browser, because a
    /// browser can't reliably say whether a window is private (<see cref="Browsers"/>). Other desktop apps and
    /// ThumbFree's own practice boxes (<paramref name="app"/> null) may be kept.
    /// </summary>
    /// <param name="app">The process name of the app the text went to ("WINWORD"); null for ThumbFree's own boxes.</param>
    public static bool MayKeep(string? app, bool passwordField) => !passwordField && !Browsers.IsBrowser(app);

    /// <summary>
    /// Keeps one dictation, on a background thread, when "Help improve ThumbFree" is on and <see cref="MayKeep"/>
    /// allows it. The host passes, for every dictation, whether it went into a password field and the app's process
    /// name. The note records the app's name, never its window title.
    /// </summary>
    /// <param name="audio">The recording: 16 kHz mono, -1 to 1.</param>
    /// <param name="recognized">What the model heard, before formatting.</param>
    /// <param name="typed">What was typed (or kept for copying).</param>
    /// <param name="app">The process name of the app the text went to ("WINWORD"); null for ThumbFree's own practice boxes.</param>
    public bool Save(float[] audio, string recognized, string typed, ModelSpec model, bool passwordField, string? app)
    {
        if (!_enabled() || !MayKeep(app, passwordField) || audio.Length == 0)
        {
            return false;
        }

        var time = _clock();
        Enqueue(() =>
        {
            try
            {
                var sample = NewSampleFolder(time);
                WriteWav(Path.Combine(sample, "audio.wav"), audio);
                var note = new Dictionary<string, object>
                {
                    ["model"] = model.Id,
                    ["language"] = model.Language.ToString(),
                    ["seconds"] = Math.Round(audio.Length / (double)Decoding.SampleRate, 2),
                    ["transcript"] = recognized,
                    ["typed"] = typed,
                    ["app"] = app ?? "ThumbFree",
                    ["app_version"] = _appVersion,
                    ["platform"] = "windows",
                    ["time"] = time.ToString("o", CultureInfo.InvariantCulture),
                };
                File.WriteAllText(Path.Combine(sample, "meta.json"), JsonSerializer.Serialize(note, NoteOptions));
                Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A full disk must never disturb dictation; the sample is simply not kept.
                Log.Warn($"sample not saved: {ex.GetType().Name}");
            }
        });
        return true;
    }

    /// <summary>How many dictations are kept and their total size.</summary>
    public SampleSummary Summary()
    {
        try
        {
            var samples = Samples();
            return new SampleSummary(samples.Count, samples.Sum(Size));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SampleSummary(0, 0);
        }
    }

    /// <summary>Puts every kept dictation into one zip (sample folders inside) and returns how many it holds.</summary>
    public Task<int> ExportZipAsync(string zipPath) => Enqueue(() =>
    {
        var samples = Samples().OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        var temp = zipPath + ".partial";
        File.Delete(temp);
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var sample in samples)
            {
                foreach (var file in sample.EnumerateFiles())
                {
                    zip.CreateEntryFromFile(file.FullName, $"{sample.Name}/{file.Name}", CompressionLevel.Optimal);
                }
            }
        }

        File.Move(temp, zipPath, overwrite: true);
        return samples.Count;
    });

    /// <summary>Deletes every kept dictation (after any that are still being saved).</summary>
    public Task DeleteAllAsync() => Enqueue(() =>
    {
        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }
    });

    /// <summary>Waits for saves and deletes still running (tests and exit).</summary>
    public bool Flush(TimeSpan timeout)
    {
        Task work;
        lock (_gate)
        {
            work = _work;
        }

        try
        {
            return work.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    /// <summary>Writes 16-bit PCM, 16 kHz, mono.</summary>
    internal static void WriteWav(string path, float[] audio)
    {
        using var writer = new BinaryWriter(File.Create(path));
        var data = audio.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + data);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Decoding.SampleRate);
        writer.Write(Decoding.SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(data);
        foreach (var sample in audio)
        {
            writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * 32767));
        }
    }

    private List<DirectoryInfo> Samples() =>
        Directory.Exists(Folder) ? [.. new DirectoryInfo(Folder).EnumerateDirectories()] : [];

    private static long Size(DirectoryInfo sample) => sample.EnumerateFiles().Sum(f => f.Length);

    /// <summary>yyyyMMdd-HHmmss-fff, so names sort oldest first (as on Android).</summary>
    private string NewSampleFolder(DateTimeOffset time)
    {
        var name = time.LocalDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(Folder, name);
        for (var i = 2; Directory.Exists(path); i++)
        {
            path = Path.Combine(Folder, $"{name}-{i}");
        }

        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Keeps the newest samples within <see cref="MaxSamples"/> and <see cref="MaxBytes"/>.</summary>
    private void Trim()
    {
        long bytes = 0;
        var newestFirst = Samples().OrderByDescending(d => d.Name, StringComparer.Ordinal).ToList();
        for (var i = 0; i < newestFirst.Count; i++)
        {
            bytes += Size(newestFirst[i]);
            if (i >= _maxSamples || bytes > _maxBytes)
            {
                newestFirst[i].Delete(recursive: true);
            }
        }
    }

    private Task Enqueue(Action work) => Enqueue(() =>
    {
        work();
        return 0;
    });

    private Task<T> Enqueue<T>(Func<T> work)
    {
        lock (_gate)
        {
            var next = _work.ContinueWith(
                _ =>
                {
                    try
                    {
                        return work();
                    }
                    finally
                    {
                        RaiseChanged();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            _work = next;
            return next;
        }
    }

    private void RaiseChanged()
    {
        if (Changed is not { } handler)
        {
            return;
        }

        if (_context is null)
        {
            handler();
        }
        else
        {
            _context.Post(_ => handler(), null);
        }
    }
}

internal readonly record struct SampleSummary(int Count, long Bytes);
