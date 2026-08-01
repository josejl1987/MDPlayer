namespace Fmp.Core.Playback.Spc;

/// <summary>Resolves duration/fade per §24: explicit → metadata → default → max.</summary>
internal static class SpcDurationResolver
{
    public const double DefaultUntaggedDurationSeconds = 150;
    public const double DefaultFadeSeconds = 8;
    public const double MaximumWithoutOverrideSeconds = 600;

    public sealed record Resolution(double DurationSeconds, double FadeSeconds, string Source);

    public static Resolution Resolve(double? explicitDuration, double? explicitFade, SpcMetadata metadata)
    {
        double duration;
        string source;
        if (explicitDuration is > 0) { duration = explicitDuration.Value; source = "explicit"; }
        else if (metadata.PlayLengthSeconds is > 0) { duration = metadata.PlayLengthSeconds.Value; source = "spc-metadata"; }
        else { duration = DefaultUntaggedDurationSeconds; source = "default"; }

        if (duration > MaximumWithoutOverrideSeconds)
            duration = MaximumWithoutOverrideSeconds;

        double fade = explicitFade is >= 0
            ? explicitFade.Value
            : metadata.FadeLengthMilliseconds is > 0
                ? metadata.FadeLengthMilliseconds.Value / 1000.0
                : DefaultFadeSeconds;

        return new Resolution(duration, fade, source);
    }
}
