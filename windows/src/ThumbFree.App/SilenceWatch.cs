namespace ThumbFree.App;

/// <summary>
/// Tells when Windows has cut ThumbFree off the microphone (PLAN 3.6): exact digital silence (all-zero samples) for more
/// than 1 s from the start, or for 3 s later on. Real rooms are never exactly silent; a call or another app taking the
/// microphone makes Windows send zeros.
/// </summary>
internal sealed class SilenceWatch(int sampleRate)
{
    private long _samples;
    private long _zeroRun;
    private bool _silentSoFar = true;

    /// <summary>The microphone sends only zeros now.</summary>
    public bool Silent { get; private set; }

    /// <summary>Adds the latest samples; true when <see cref="Silent"/> changed.</summary>
    public bool Add(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            if (sample == 0)
            {
                _zeroRun++;
            }
            else
            {
                _zeroRun = 0;
                _silentSoFar = false;
            }
        }

        _samples += samples.Length;
        var silent = (_silentSoFar && _samples > sampleRate) || _zeroRun > sampleRate * 3L;
        if (silent == Silent)
        {
            return false;
        }

        Silent = silent;
        return true;
    }
}
