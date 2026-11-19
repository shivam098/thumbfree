using System.Diagnostics;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using ThumbFree.App.Platform;
using ThumbFree.Core;
using ThumbFree.Speech;

namespace ThumbFree.App;

/// <summary>
/// Runs a script through the real dictation host and Windows input, so dictation into real apps can be checked end to
/// end without anyone speaking. Script lines:
///   ptt: &lt;transcript&gt;          a dictation typed when it ends (as with Live typing off)
///   live: &lt;a&gt; | &lt;b&gt; | &lt;c&gt;     a dictation whose phrases come one by one, each typed once it's final (live typing)
///   wav: &lt;file&gt;              a recording played as if spoken into the microphone, through the chosen model
///   model &lt;id&gt;               the model for wav: (en-kroko-live, en-parakeet-110m, en-parakeet-0.6b, hinglish-swift…)
///   livetyping on|off        the Live typing switch for wav:
///   field                    record what kind of text box has the focus (never its text)
///   expectfield: &lt;words&gt;    PASS/FAIL: the focused box's kind has every word ("multi-line email", "password")
///   again                    paste the last dictation again (Alt+Shift+Z)
///   wait &lt;ms&gt;
///   fg                       record the foreground process name
///   read                     record the focused control's type, text length and hash (not its text)
///   expect: &lt;text&gt;           PASS/FAIL: does the focused control contain the script's phrase (\n = line break)
///   expectnot: &lt;text&gt;        PASS/FAIL: the focused control must NOT contain the phrase (e.g. refused password field)
///   keys: &lt;chord&gt;            press keys, e.g. "keys: Tab" to move to the next field
///   expectstatus: &lt;text&gt;     PASS/FAIL: does the latest status message contain the phrase
///   expecttitle: &lt;text&gt;      PASS/FAIL: does the foreground window title contain the phrase (title not written)
///   expectfocus: &lt;name&gt;      PASS/FAIL: does the focused control's accessible name contain the phrase
///   insert Paste|Type        switch the insertion method
///   stoponfail               stop the script at the first failed title or focus check (the test window or box isn't
///                            in front); after a passing expecttitle, nothing is typed unless that window is in front
///   clipboard                record a fingerprint (formats, hash, length) of the clipboard, not its text
///   # comment
/// The output never contains text from your windows, titles or clipboard: only the script's own text, what the test
/// recordings were heard as, and the checks. History and samples go to a temporary folder that is deleted afterwards.
/// </summary>
internal static class SelfTest
{
    public static async Task RunAsync(string scriptPath, string outputPath, Dispatcher ui)
    {
        // Status lines arrive on the UI thread while the script runs on others.
        var output = new StringBuilder();
        void Line(string text)
        {
            lock (output)
            {
                output.AppendLine(text);
            }
        }

        var temp = Path.Combine(Path.GetTempPath(), "thumbfree-selftest-" + Environment.ProcessId);
        Directory.CreateDirectory(temp);
        // Default settings, so results don't depend on this PC's (they're never written: the store saves to the temp folder).
        var settings = new AppSettings();
        var store = new SettingsStore(settings, Path.Combine(temp, "settings.json"));
        var models = new ModelStore(AppPaths.ModelsDir, AppPaths.SharedModelsDirs);
        var services = new AppServices
        {
            Settings = store,
            History = new HistoryStore(Path.Combine(temp, "history.txt"), () => false),
            Samples = new SampleStore(Path.Combine(temp, "samples"), () => false, "self-test"),
            Models = new ModelDownloads(new StoreModelManager(models)),
            State = new DictationState(),
            Environment = new WindowsEnvironment(),
        };
        var recording = new WavSource();
        using var host = new DictationHost(services, new StoreSpeechModels(models), action => ui.BeginInvoke(action), new WindowsDesktop(), new UiaFieldReader(), recording, sounds: false);
        var lastStatus = string.Empty;
        host.Status += s =>
        {
            lastStatus = s.Message;
            Line($"   {s.Kind}: {Redact(s.Message)}");
        };

        // The same on-screen feedback as the app, so screenshots of a self-test show what a user would see.
        var pill = new UI.OverlayWindow { Pinned = true };
        host.Status += pill.ShowStatus;
        host.Activity += pill.ShowActivity;
        host.DictationPreview += pill.ShowPreview;
        try
        {
            var stopOnFail = false;
            var stop = false;
            nint labWindow = 0;
            var dictations = 0;
            host.Start();
            foreach (var raw in await File.ReadAllLinesAsync(scriptPath).ConfigureAwait(false))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                Line($"> {line}");
                if (stop)
                {
                    // The expected window or box isn't in front: typing on could go somewhere else.
                    Line("!! stopped after a failed check");
                    break;
                }

                if (labWindow != 0 && TypesOrPresses(line) && new WindowsDesktop().GetForegroundWindow()?.Handle != labWindow)
                {
                    Line("!! the test window isn't in front any more; stopped before typing");
                    break;
                }

                if (line == "stoponfail")
                {
                    stopOnFail = true;
                }
                else if (line.StartsWith("ptt:", StringComparison.Ordinal))
                {
                    Line(Describe(await host.DictateAsync([line[4..].Trim()], live: false).ConfigureAwait(false)));
                }
                else if (line.StartsWith("live:", StringComparison.Ordinal))
                {
                    var phrases = line[5..].Split('|').Select(p => p.Trim()).ToList();
                    Line(Describe(await host.DictateAsync(phrases, live: true).ConfigureAwait(false)));
                }
                else if (line.StartsWith("wav:", StringComparison.Ordinal))
                {
                    var path = line[4..].Trim();
                    path = Path.IsPathRooted(path) ? path : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scriptPath))!, path);
                    var outcome = await PlayAsync(host, recording, WavFile.ReadPcm16Mono16k(path), ++dictations, ui).ConfigureAwait(false);
                    Line($"   heard (test recording): {outcome.Text.Replace("\n", "\\n", StringComparison.Ordinal)}");
                    Line(Describe(outcome));
                }
                else if (line.StartsWith("model ", StringComparison.Ordinal))
                {
                    var spec = ModelCatalog.ById(line[6..].Trim()) ?? throw new ArgumentException($"No model {line[6..].Trim()}.");
                    await ui.InvokeAsync(() => store.Update(s => Choose(s, spec)));
                    Line($"   model: {spec.Id} ({(new StoreSpeechModels(models).IsInstalled(spec) ? "installed" : "NOT installed")})");
                }
                else if (line.StartsWith("livetyping ", StringComparison.Ordinal))
                {
                    var on = line.EndsWith("on", StringComparison.Ordinal);
                    await ui.InvokeAsync(() => store.Update(s => s.LiveTyping = on));
                }
                else if (line == "field" || line.StartsWith("expectfield:", StringComparison.Ordinal))
                {
                    var window = new WindowsDesktop().GetForegroundWindow();
                    var field = window is null ? null : FocusInspector.Read(window);
                    var kind = field?.Kind ?? "unknown";
                    if (line == "field")
                    {
                        Line($"   field: {kind}; text before the cursor: {(field?.TextBefore is { } before ? $"{before.Length} chars{(field.CaretKnown ? string.Empty : ", cursor assumed at the end")}" : "unknown")}");
                    }
                    else
                    {
                        var words = line[12..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var pass = words.All(w => kind.Contains(w, StringComparison.OrdinalIgnoreCase));
                        Line($"   {(pass ? "PASS" : "FAIL")}: field is {kind}");
                    }
                }
                else if (line == "again")
                {
                    host.PasteLastTranscript();
                    await host.RunOnQueueAsync(() => Task.CompletedTask).ConfigureAwait(false);
                }
                else if (line.StartsWith("wait ", StringComparison.Ordinal))
                {
                    await Task.Delay(int.Parse(line[5..])).ConfigureAwait(false);
                }
                else if (line.StartsWith("typing ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ');
                    KeyboardInput.TypingChunk = int.Parse(parts[1]);
                    KeyboardInput.TypingDelayMs = int.Parse(parts[2]);
                }
                else if (line.StartsWith("insert ", StringComparison.Ordinal))
                {
                    WindowsDesktop.InsertMethod = line[7..].Trim();
                }
                else if (line == "clipboard")
                {
                    Line($"   clipboard fingerprint: {ClipboardFingerprint()}");
                }
                else if (line == "fg")
                {
                    // Process name only: window titles can contain the user's document or page names.
                    var fg = new WindowsDesktop().GetForegroundWindow();
                    Line($"   foreground: {fg?.ProcessName}");
                }
                else if (line == "read")
                {
                    var text = ReadFocusedText();
                    Line($"   focused: {text.Type} len={text.Value.Length} hash={Hash(text.Value)}");
                }
                else if (line.StartsWith("expectfocus:", StringComparison.Ordinal))
                {
                    // PASS/FAIL: is the focused control the one with this accessible name (a label from the test page). Chrome
                    // builds its accessibility tree on the first query, so this waits up to 3 s for it.
                    var expected = line[12..].Trim();
                    var name = FocusedName();
                    for (var tries = 0; tries < 12 && !name.Contains(expected, StringComparison.OrdinalIgnoreCase); tries++)
                    {
                        await Task.Delay(250).ConfigureAwait(false);
                        name = FocusedName();
                    }

                    var pass = name.Contains(expected, StringComparison.OrdinalIgnoreCase);
                    Line($"   {(pass ? "PASS" : "FAIL")}: focused control {(pass ? "has" : "does not have")} the expected name{(pass ? string.Empty : $" (focused: {ReadFocusedText().Type}; reported by UI Automation: {DescribeReportedFocus()})")}");
                    stop |= stopOnFail && !pass;
                }
                else if (line.StartsWith("expecttitle:", StringComparison.Ordinal))
                {
                    // PASS/FAIL only: the title itself is never written. From here on, nothing is typed unless this window
                    // is still in front.
                    var expected = line[12..].Trim();
                    var foreground = new WindowsDesktop().GetForegroundWindow();
                    var pass = (foreground?.Title ?? string.Empty).Contains(expected, StringComparison.OrdinalIgnoreCase);
                    Line($"   {(pass ? "PASS" : "FAIL")}: foreground title {(pass ? "contains" : "does not contain")} the expected phrase");
                    stop |= stopOnFail && !pass;
                    labWindow = pass ? foreground!.Handle : labWindow;
                }
                else if (line.StartsWith("keys:", StringComparison.Ordinal))
                {
                    new WindowsDesktop().PressKeys(line[5..].Trim());
                }
                else if (line.StartsWith("expectstatus:", StringComparison.Ordinal))
                {
                    // Status posts arrive through the UI dispatcher; give them a moment.
                    await Task.Delay(300).ConfigureAwait(false);
                    var pass = lastStatus.Contains(line[13..].Trim(), StringComparison.OrdinalIgnoreCase);
                    Line($"   {(pass ? "PASS" : "FAIL")}: last status {(pass ? "contains" : "does not contain")} the phrase");
                }
                else if (line.StartsWith("expectnot:", StringComparison.Ordinal))
                {
                    var unexpected = line[10..].Trim();
                    var actual = ReadFocusedText().Value;
                    var pass = !actual.Contains(unexpected, StringComparison.Ordinal);
                    Line($"   {(pass ? "PASS" : "FAIL")}: focused text {(pass ? "does not contain" : "contains")} the phrase (field len={actual.Length})");
                }
                else if (line.StartsWith("expect:", StringComparison.Ordinal))
                {
                    // Records only whether the script's own phrase is present, never the field's other contents.
                    var expected = line[7..].Trim().Replace("\\n", "\n", StringComparison.Ordinal);
                    var actual = ReadFocusedText().Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\v', '\n');
                    var pass = actual.Contains(expected, StringComparison.Ordinal);
                    Line($"   {(pass ? "PASS" : "FAIL")}: focused text {(pass ? "contains" : "does not contain")} the expected phrase (field len={actual.Length})");
                }
                else
                {
                    Line("   ?? unknown command");
                }

                await Task.Delay(400).ConfigureAwait(false);
            }

            // Let queued UI status posts land.
            await Task.Delay(500).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Line($"!! {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        string written;
        lock (output)
        {
            written = output.ToString();
        }

        await File.WriteAllTextAsync(outputPath, written).ConfigureAwait(false);
    }

    /// <summary>Plays <paramref name="audio"/> as if spoken now: Ctrl+Win, the recording in real time, Ctrl+Win.</summary>
    private static async Task<DictationOutcome> PlayAsync(DictationHost host, WavSource recording, float[] audio, int id, Dispatcher ui)
    {
        var finished = new TaskCompletionSource<DictationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(DictationOutcome outcome) => finished.TrySetResult(outcome);
        host.Finished += OnFinished;
        try
        {
            recording.Load(audio);
            await ui.InvokeAsync(() => host.BeginDictationCapture(id));
            var played = await Task.WhenAny(recording.Played.Task, Task.Delay(TimeSpan.FromSeconds(audio.Length / 16_000.0 + 30))).ConfigureAwait(false);
            if (played != recording.Played.Task)
            {
                throw new TimeoutException("The recording never started: is the model installed?");
            }

            // A tap a moment after the last word (the test clips end with about 0.8 s of silence already).
            await Task.Delay(300).ConfigureAwait(false);
            await ui.InvokeAsync(() => host.EndDictationCapture(cancel: false));
            var done = await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false);
            return done == finished.Task ? finished.Task.Result : throw new TimeoutException("The dictation didn't finish within 60 s.");
        }
        finally
        {
            host.Finished -= OnFinished;
        }
    }

    private static string Describe(DictationOutcome outcome) =>
        $"   {(outcome.TypedAll ? "typed" : outcome.Kept ? "kept for copying" : "not typed: " + outcome.Problem)} (len={outcome.Text.Length})";

    /// <summary>Script lines that type or press keys: they run only while the test window is in front.</summary>
    private static bool TypesOrPresses(string line) =>
        line == "again" || line.StartsWith("keys:", StringComparison.Ordinal) || line.StartsWith("ptt:", StringComparison.Ordinal)
        || line.StartsWith("live:", StringComparison.Ordinal) || line.StartsWith("wav:", StringComparison.Ordinal);

    /// <summary>Selects <paramref name="spec"/> the way Settings would: its language, and its tier for that language.</summary>
    private static void Choose(AppSettings settings, ModelSpec spec)
    {
        settings.Language = spec.Language;
        var tier = spec.Streaming ? Tier.Live : spec.Id == ModelCatalog.EnglishAccurate.Id || spec.Id == ModelCatalog.HinglishAccurate.Id ? Tier.Accurate : Tier.Standard;
        if (spec.Language == Language.Hinglish)
        {
            settings.HinglishTier = tier;
        }
        else
        {
            settings.EnglishTier = tier;
        }
    }

    /// <summary>Formats + a hash of the text, so restoration can be verified without recording the user's clipboard.</summary>
    private static string ClipboardFingerprint()
    {
        var result = string.Empty;
        var t = new Thread(() =>
        {
            var data = System.Windows.Clipboard.GetDataObject();
            var formats = data is null ? "(empty)" : string.Join(",", data.GetFormats(false).Take(6));
            var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
            result = $"formats=[{formats}] textHash={hash} len={text.Length}";
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }

    private static string Hash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    /// <summary>Names, titles and heard text appear in “quotes” in status messages; keep them out of the output.</summary>
    private static string Redact(string message) =>
        System.Text.RegularExpressions.Regex.Replace(message, "“[^”]*”", "“…”");

    private static string FocusedName()
    {
        try
        {
            var task = Task.Run(() => Platform.FocusInspector.FocusedElement()?.Current.Name ?? string.Empty);
            return task.Wait(3000) ? task.Result : string.Empty;
        }
        catch (AggregateException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// What UI Automation itself reports as focused (before a Chromium pane is looked into): its kind, class and framework,
    /// never its name or text. Says why an expected box wasn't found, such as a browser bubble that has the focus.
    /// </summary>
    private static string DescribeReportedFocus()
    {
        try
        {
            var task = Task.Run(() =>
            {
                var current = AutomationElement.FocusedElement.Current;
                return $"{current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal)} class '{current.ClassName}' framework '{current.FrameworkId}'";
            });
            return task.Wait(3000) ? task.Result : "(timeout)";
        }
        catch (AggregateException ex)
        {
            return $"({ex.InnerException?.GetType().Name})";
        }
    }

    private static (string Type, string Value) ReadFocusedText()
    {
        try
        {
            var task = Task.Run(() =>
            {
                var e = Platform.FocusInspector.FocusedElement();
                if (e is null)
                {
                    return ("(none)", string.Empty);
                }

                var type = e.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal);
                if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var v))
                {
                    return (type, ((ValuePattern)v).Current.Value);
                }

                if (e.TryGetCurrentPattern(TextPattern.Pattern, out var t))
                {
                    return (type, ((TextPattern)t).DocumentRange.GetText(4000));
                }

                return (type, string.Empty);
            });
            return task.Wait(3000) ? task.Result : ("(timeout)", string.Empty);
        }
        catch (Exception ex)
        {
            return ($"(error {ex.GetType().Name})", string.Empty);
        }
    }
}

/// <summary>
/// Self-tests: a recording played as if spoken into the microphone, in real time and in 30 ms buffers like the
/// microphone's, then faint noise (a real room) until dictation stops.
/// </summary>
internal sealed class WavSource : IAudioSource
{
    private const int BufferSamples = 480;
    private float[] _audio = [];
    private volatile bool _running;
    private Thread? _thread;

    public event Action<float[]>? Samples;

    public event Action<Exception>? Failed
    {
        add { }
        remove { }
    }

    /// <summary>Completes when the whole recording has been played.</summary>
    public TaskCompletionSource Played { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Load(float[] audio)
    {
        _audio = audio;
        Played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Start()
    {
        Stop();
        _running = true;
        _thread = new Thread(Play) { IsBackground = true, Name = "self-test recording" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(1));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Play()
    {
        var clock = Stopwatch.StartNew();
        var noise = new Random(1);
        var sent = 0;
        while (_running)
        {
            while (_running && sent + BufferSamples <= clock.ElapsedMilliseconds * 16)
            {
                var buffer = new float[BufferSamples];
                for (var i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = sent + i < _audio.Length ? _audio[sent + i] : (float)(noise.NextDouble() - 0.5) * 0.0004f;
                }

                sent += buffer.Length;
                Samples?.Invoke(buffer);
                if (sent >= _audio.Length)
                {
                    Played.TrySetResult();
                }
            }

            Thread.Sleep(5);
        }
    }
}
