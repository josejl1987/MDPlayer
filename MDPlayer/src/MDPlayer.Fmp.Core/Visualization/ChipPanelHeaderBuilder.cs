using Fmp.Core.Decoding.SnesDsp;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Core.Visualization;

/// <summary>
/// Adapts chip-specific state into the generic panel-header contract. The
/// renderer consumes only PanelHeaderData and remains format-agnostic.
/// </summary>
internal static class ChipPanelHeaderBuilder
{
    public static bool TryBuild(
        PreparedPanel panel,
        long currentSample,
        out PanelHeaderData header)
    {
        header = null;
        if (panel.SpcVoiceStates.Length == 0)
            return false;

        string voiceId = panel.SpcVoiceStates[0].VoiceId;
        int separator = voiceId.LastIndexOf('.');
        if (separator < 0
            || !int.TryParse(voiceId[(separator + 1)..], out int voiceIndex))
            return false;
        // Some rips emit events for a pseudo-voice outside the 8-voice S-DSP
        // range (e.g. a key-off broadcast). Those panels cannot carry a
        // per-voice chip header; fall back to the generic label.
        if (voiceIndex < 0 || voiceIndex >= SnesDspPresentation.VoiceCount)
            return false;

        int source = 0;
        sbyte volumeLeft = 127;
        sbyte volumeRight = 127;
        bool noise = false;
        bool pitchMod = false;
        bool echoSend = false;
        foreach (SpcVoiceStateEvent state in panel.SpcVoiceStates)
        {
            if (state.SamplePosition > currentSample)
                break;
            switch (state.State)
            {
                case nameof(SpcSemanticEventKind.SourceLatched):
                    source = state.Value;
                    break;
                case nameof(SpcSemanticEventKind.VolumeChanged):
                    volumeLeft = unchecked((sbyte)state.Value);
                    volumeRight = unchecked((sbyte)state.Value2);
                    break;
                case nameof(SpcSemanticEventKind.NoiseChanged):
                    noise = state.Value != 0;
                    break;
                case nameof(SpcSemanticEventKind.PitchModChanged):
                    pitchMod = state.Value != 0;
                    break;
                case nameof(SpcSemanticEventKind.EchoSendChanged):
                    echoSend = state.Value != 0;
                    break;
            }
        }

        string shortHash = "";
        foreach (SamplePlaybackEvent playback in panel.SamplePlayback)
        {
            if (playback.StartSample > currentSample)
                break;
            if (playback.StartSample <= currentSample && currentSample < playback.EndSample)
            {
                int hashSeparator = playback.SampleId.LastIndexOf(':');
                shortHash = hashSeparator >= 0
                    ? playback.SampleId[(hashSeparator + 1)..]
                    : playback.SampleId;
                break;
            }
        }

        SnesDspPanelHeader chipHeader = SnesDspPresentation.BuildHeader(
            voiceIndex,
            source,
            shortHash,
            noise,
            pitchMod,
            echoSend,
            volumeLeft,
            volumeRight);
        header = new PanelHeaderData(chipHeader.Label);
        return true;
    }
}
