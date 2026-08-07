#nullable enable

namespace Fmp.Core.Timing;

/// <summary>
/// Thrown when a trustworthy musical-time grid cannot be established and the
/// caller (e.g. a <c>--strict-timing</c> export) requires one rather than
/// silently guessing phase or tempo.
/// </summary>
internal sealed class MusicalTimingException : InvalidOperationException
{
    public MusicalTimingException(string message) : base(message)
    {
    }

    public MusicalTimingException(string message, Exception inner) : base(message, inner)
    {
    }
}
