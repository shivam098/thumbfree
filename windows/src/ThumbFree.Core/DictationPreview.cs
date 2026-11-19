namespace ThumbFree.Core;

/// <summary>
/// Live transcript while tap-to-dictate dictation is recording. Each update re-transcribes the
/// audio after the last committed point; once that exceeds the recognizer window, the oldest part is
/// transcribed once, cut at a pause, and kept, so each update costs at most a window of audio.
/// With phrases on (live typing with a model that isn't streaming), every pause also ends a phrase: its audio is
/// transcribed once, at final quality, and never changes, so it can be typed (<see cref="LiveTyping"/>).
/// </summary>
public sealed class DictationPreview
{
    private readonly Func<float[], Task<string>> _transcribe;
    private readonly Func<float[], Task<string>> _commit;
    private readonly Func<float[], IReadOnlyList<(TimeSpan Start, TimeSpan End)>>? _detect;
    private readonly int _sampleRate;
    private readonly int _windowSamples;
    private readonly int _minCommitSamples;
    private readonly int? _phrasePauseSamples;
    private readonly List<string> _committed = [];

    /// <param name="transcribe">Returns cleaned text for the audio (empty when nothing was said).</param>
    /// <param name="detect">Speech regions in the audio; null treats all audio as speech.</param>
    /// <param name="phrasePauseMs">A pause at least this long ends a phrase, which is then kept; null keeps text only past the window.</param>
    /// <param name="commit">Transcribes audio that is kept (final quality); <paramref name="transcribe"/> when null.</param>
    public DictationPreview(
        Func<float[], Task<string>> transcribe,
        Func<float[], IReadOnlyList<(TimeSpan Start, TimeSpan End)>>? detect,
        int sampleRate,
        int windowMs = 12000,
        int minCommitMs = 3000,
        int? phrasePauseMs = null,
        Func<float[], Task<string>>? commit = null)
    {
        _transcribe = transcribe;
        _commit = commit ?? transcribe;
        _detect = detect;
        _sampleRate = sampleRate;
        _windowSamples = sampleRate * windowMs / 1000;
        _minCommitSamples = Math.Min(sampleRate * minCommitMs / 1000, _windowSamples);
        _phrasePauseSamples = phrasePauseMs is { } pause ? sampleRate * pause / 1000 : null;
    }

    /// <summary>Samples from the start of the recording that are already transcribed and kept.</summary>
    public int CommittedSamples { get; private set; }

    /// <summary>The kept text, one entry per finished phrase (or window), in order; never changes once added.</summary>
    public IReadOnlyList<string> Phrases => _committed;

    /// <summary>The latest transcription of the audio after <see cref="CommittedSamples"/> (the phrase being spoken).</summary>
    public string Tentative { get; private set; } = string.Empty;

    /// <summary>Transcribes <paramref name="pending"/> (the audio recorded after <see cref="CommittedSamples"/>).</summary>
    public async Task<string> UpdateAsync(float[] pending)
    {
        var speech = Detect(pending);
        while (_phrasePauseSamples is { } pause && FindPhraseEnd(speech, pending.Length, _sampleRate, pause) is { } end)
        {
            var text = await _commit(pending[..end]).ConfigureAwait(false);
            if (text.Length > 0)
            {
                _committed.Add(text);
            }

            CommittedSamples += end;
            pending = pending[end..];
            speech = Detect(pending);
        }

        while (pending.Length > _windowSamples)
        {
            var cut = FindCommitPoint(speech, pending.Length, _sampleRate, _windowSamples, _minCommitSamples);
            if (speech.Any(s => (int)(s.Start.TotalSeconds * _sampleRate) < cut))
            {
                var text = await _commit(pending[..cut]).ConfigureAwait(false);
                if (text.Length > 0)
                {
                    _committed.Add(text);
                }
            }

            CommittedSamples += cut;
            pending = pending[cut..];
            speech = Detect(pending);
        }

        Tentative = speech.Count == 0 ? string.Empty : await _transcribe(pending).ConfigureAwait(false);
        return string.Join(" ", _committed.Append(Tentative).Where(t => t.Length > 0));
    }

    /// <summary>
    /// The end of the first phrase that a pause of at least <paramref name="pauseSamples"/> has finished (speech, then that
    /// much silence, either before more speech or at the end so far): the speech's end plus up to 0.3 s. Null if none yet.
    /// </summary>
    public static int? FindPhraseEnd(IReadOnlyList<(TimeSpan Start, TimeSpan End)> speech, int length, int sampleRate, int pauseSamples)
    {
        var regions = speech
            .Select(s => (Start: (int)(s.Start.TotalSeconds * sampleRate), End: Math.Min(length, (int)(s.End.TotalSeconds * sampleRate))))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();
        var reach = 0;
        for (var i = 0; i < regions.Count; i++)
        {
            reach = Math.Max(reach, regions[i].End);
            var next = i + 1 < regions.Count ? regions[i + 1].Start : length;
            var gap = next - reach;
            if (gap >= pauseSamples)
            {
                return Math.Min(length, reach + Math.Min(gap / 2, sampleRate * 3 / 10));
            }
        }

        return null;
    }

    /// <summary>
    /// Where to cut audio longer than the window: in the latest pause between <paramref name="minSamples"/> and
    /// <paramref name="windowSamples"/>, else the window edge. Audio with no speech is dropped except its last second.
    /// </summary>
    public static int FindCommitPoint(
        IReadOnlyList<(TimeSpan Start, TimeSpan End)> speech,
        int length,
        int sampleRate,
        int windowSamples,
        int minSamples)
    {
        if (speech.Count == 0)
        {
            return Math.Max(1, length - sampleRate);
        }

        var regions = speech
            .Select(s => (Start: (int)(s.Start.TotalSeconds * sampleRate), End: (int)(s.End.TotalSeconds * sampleRate)))
            .OrderBy(s => s.Start)
            .ToList();
        var candidates = regions.Zip(regions.Skip(1), (a, b) => (a.End + b.Start) / 2).ToList();
        if (regions[^1].End < length)
        {
            candidates.Add(Math.Min(length, regions[^1].End + (sampleRate / 5)));
        }

        var best = candidates.Where(c => c >= minSamples && c <= windowSamples).DefaultIfEmpty(0).Max();
        return best > 0 ? best : windowSamples;
    }

    private IReadOnlyList<(TimeSpan Start, TimeSpan End)> Detect(float[] audio) =>
        _detect is null ? [(TimeSpan.Zero, TimeSpan.FromSeconds((double)audio.Length / _sampleRate))] : _detect(audio);
}
