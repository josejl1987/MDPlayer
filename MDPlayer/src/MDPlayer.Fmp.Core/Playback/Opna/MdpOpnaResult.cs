namespace Fmp.Core.Playback.Opna;

/// <summary>
/// Result codes returned by the native <c>mdplayer_opna</c> ABI.
/// Mirrors <c>mdp_opna_result</c> in <c>mdplayer_opna.h</c> (ABI version 1).
/// </summary>
public enum MdpOpnaResult
{
    /// <summary>Operation succeeded.</summary>
    Ok = 0,

    /// <summary>Invalid argument (bad pointer, unsupported bank, negative frame count).</summary>
    InvalidArgument = -1,

    /// <summary>Native allocation failed.</summary>
    OutOfMemory = -2,

    /// <summary>Requested master clock regresses below the current clock.</summary>
    ClockRegression = -3,

    /// <summary>The timed-audio FIFO filled while advancing.</summary>
    FifoOverflow = -4,

    /// <summary>The requested output rate is not one of 44100/48000/96000.</summary>
    UnsupportedRate = -5,

    /// <summary>The observed frame cadence is not the fixed 144-clock profile.</summary>
    UnsupportedCadence = -6,

    /// <summary>Internal invariant failure inside the native library.</summary>
    Internal = -7,
}
