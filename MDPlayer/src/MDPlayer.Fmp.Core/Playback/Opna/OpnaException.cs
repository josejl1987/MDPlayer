using System;

namespace Fmp.Core.Playback.Opna;

/// <summary>
/// Base for every error raised by the managed OPNA (YM2608) integration layer.
/// </summary>
public class OpnaException : Exception
{
    /// <inheritdoc />
    public OpnaException(string message)
        : base(message)
    {
    }

    /// <inheritdoc />
    public OpnaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The native <c>mdplayer_opna</c> library could not be located or loaded.
/// The message explains the supported resolution paths.
/// </summary>
public sealed class OpnaLibraryNotFoundException : OpnaException
{
    /// <inheritdoc />
    public OpnaLibraryNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A native <c>mdp_opna_*</c> call returned a failure result code.
/// Carries the native <see cref="MdpOpnaResult"/> for precise handling.
/// </summary>
public class OpnaNativeException : OpnaException
{
    /// <summary>The native result code returned by the library.</summary>
    public MdpOpnaResult Code { get; }

    /// <inheritdoc />
    public OpnaNativeException(MdpOpnaResult code, string message)
        : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// A clock advance request regressed below the current absolute master clock.
/// </summary>
public sealed class OpnaClockRegressionException : OpnaNativeException
{
    /// <inheritdoc />
    public OpnaClockRegressionException(string message)
        : base(MdpOpnaResult.ClockRegression, message)
    {
    }
}

/// <summary>
/// Advancing filled the native timed-audio FIFO; the caller must drain before
/// advancing further.
/// </summary>
public sealed class OpnaFifoOverflowException : OpnaNativeException
{
    /// <inheritdoc />
    public OpnaFifoOverflowException(string message)
        : base(MdpOpnaResult.FifoOverflow, message)
    {
    }
}

/// <summary>
/// The requested output rate is not supported (only 44100, 48000 and 96000).
/// </summary>
public sealed class OpnaUnsupportedRateException : OpnaNativeException
{
    /// <inheritdoc />
    public OpnaUnsupportedRateException(string message)
        : base(MdpOpnaResult.UnsupportedRate, message)
    {
    }
}

/// <summary>
/// The observed serial frame cadence is not the fixed 144-clock profile, so
/// deterministic fixed-rate resampling cannot continue.
/// </summary>
public sealed class OpnaUnsupportedCadenceException : OpnaNativeException
{
    /// <inheritdoc />
    public OpnaUnsupportedCadenceException(string message)
        : base(MdpOpnaResult.UnsupportedCadence, message)
    {
    }
}

/// <summary>
/// The native call received an invalid argument (bad bank, bad buffer length...).
/// </summary>
public sealed class OpnaInvalidArgumentException : OpnaNativeException
{
    /// <inheritdoc />
    public OpnaInvalidArgumentException(string message)
        : base(MdpOpnaResult.InvalidArgument, message)
    {
    }
}
