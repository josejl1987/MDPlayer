namespace Fmp.Core.Rendering;

/// <summary>
/// Decides the effective end of a playback capture. A natural end gets only
/// the configured tail; a requested loop limit gets the fade followed by the
/// tail. Keeping this state in one place keeps timeline and audio capture
/// synchronized.
/// </summary>
internal sealed class PlaybackTermination
{
    private readonly int _loopCount;
    private readonly long _fadeSamples;
    private readonly long _tailSamples;
    private bool _previousPlaybackEnded;

    public PlaybackTermination(int loopCount, long fadeSamples, long tailSamples)
    {
        if (loopCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(loopCount));
        if (fadeSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(fadeSamples));
        if (tailSamples < 0)
            throw new ArgumentOutOfRangeException(nameof(tailSamples));

        _loopCount = loopCount;
        _fadeSamples = fadeSamples;
        _tailSamples = tailSamples;
    }

    public bool Started { get; private set; }
    public bool FadeActive { get; private set; }
    public long FadeStartSample { get; private set; }
    public long StopAtSample { get; private set; }
    public string StopReason { get; private set; } = "";

    public void Observe(bool playbackEnded, int currentLoop, long samplePosition)
    {
        if (Started)
        {
            _previousPlaybackEnded = playbackEnded;
            return;
        }

        bool playbackEndedEdge = playbackEnded && !_previousPlaybackEnded;
        _previousPlaybackEnded = playbackEnded;

        if (currentLoop >= _loopCount)
        {
            Start(samplePosition, fade: true, "loop_limit");
        }
        else if (playbackEndedEdge && currentLoop == 0)
        {
            // FMP reports a non-looping song completion with AX==0 while its
            // loop counter remains zero.
            Start(samplePosition, fade: false, "natural_stop");
        }
    }

    public bool IsComplete(long samplePosition)
        => Started && samplePosition >= StopAtSample;

    private void Start(long samplePosition, bool fade, string reason)
    {
        Started = true;
        FadeActive = fade;
        FadeStartSample = fade ? samplePosition : 0;
        StopReason = reason;
        StopAtSample = checked(samplePosition + (fade ? _fadeSamples : 0) + _tailSamples);
    }
}
