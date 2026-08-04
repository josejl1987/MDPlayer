namespace Fmp.Core.Rendering;

/// <summary>
/// A pull-based FMP host-PCM session at the backend-selection boundary. The
/// factory (<see cref="FmpPcmSessionFactory"/>) returns the session for the
/// requested <see cref="FmpOpnaBackend"/>; each implementation owns its chip
/// machinery and drives the FMP driver with its own timing model. Sessions are
/// deterministic and never fall back to another backend.
/// </summary>
internal interface IFmpPcmSession : IDisposable
{
    /// <summary>Output stereo sample rate of this session.</summary>
    int OutputSampleRate { get; }

    /// <summary>True once the session can produce no further samples.</summary>
    bool IsCompleted { get; }

    /// <summary>True when the render ended because the song finished naturally.</summary>
    bool NaturallyStopped { get; }

    /// <summary>Stop reason reported to the caller ("natural_stop", "loop_limit", ...).</summary>
    string StopReason { get; }

    /// <summary>Provides the track payload before <see cref="Boot"/>.</summary>
    void LoadTrack(byte[] trackData, string trackFileName);

    /// <summary>Boots FMP.COM and loads the track. Throws on failure.</summary>
    void Boot();

    /// <summary>
    /// Renders up to <c>interleavedStereo.Length / 2</c> stereo frames of
    /// 16-bit interleaved PCM and returns the number of frames produced.
    /// Returning fewer than requested means the session reached its end.
    /// </summary>
    int Render(Span<short> interleavedStereo);
}