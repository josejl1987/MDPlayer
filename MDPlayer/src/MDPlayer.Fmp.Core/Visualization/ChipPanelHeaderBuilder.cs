using Fmp.Core.Decoding.SnesDsp;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Core.Visualization;

/// <summary>
/// Adapts chip-specific state into the generic panel-header contract. The
/// renderer consumes only PanelHeaderData and remains format-agnostic.
/// </summary>
internal static class ChipPanelHeaderBuilder
{
    /// <summary>
    /// Reusable sequential cursor for one prepared S-DSP panel. Header state is
    /// source state, so advancing the cursor is equivalent to applying all state
    /// changes up to the current frame; it must not rescan either history.
    /// </summary>
    internal sealed class Cursor
    {
        internal int VoiceStateIndex;
        internal int PlaybackIndex;
        internal int PlaybackIdentityIndex = -1;
        internal long LastSample = long.MinValue;
        internal int VoiceIndex = -1;
        internal int Source;
        internal sbyte VolumeLeft = 127;
        internal sbyte VolumeRight = 127;
        internal bool Noise;
        internal bool PitchMod;
        internal bool EchoSend;
        internal string ShortHash = "";
        internal PanelHeaderData Header;
        internal int HeaderSource;
        internal sbyte HeaderVolumeLeft;
        internal sbyte HeaderVolumeRight;
        internal bool HeaderNoise;
        internal bool HeaderPitchMod;
        internal bool HeaderEchoSend;

        internal void Rewind()
        {
            VoiceStateIndex = 0;
            PlaybackIndex = 0;
            PlaybackIdentityIndex = -1;
            LastSample = long.MinValue;
            Source = 0;
            VolumeLeft = 127;
            VolumeRight = 127;
            Noise = false;
            PitchMod = false;
            EchoSend = false;
            ShortHash = "";
            Header = null;
            HeaderSource = 0;
            HeaderVolumeLeft = 0;
            HeaderVolumeRight = 0;
            HeaderNoise = false;
            HeaderPitchMod = false;
            HeaderEchoSend = false;
        }
    }

    public static bool TryBuild(
        PreparedPanel panel,
        long currentSample,
        out PanelHeaderData header)
    {
        return TryBuild(panel, currentSample, new Cursor(), out header);
    }

    public static bool TryBuild(
        PreparedPanel panel,
        long currentSample,
        Cursor cursor,
        out PanelHeaderData header)
    {
        header = null;
        if (panel.SpcVoiceStates.Length == 0)
            return false;

        string voiceId = panel.SpcVoiceStates[0].VoiceId;
        int separator = voiceId.LastIndexOf('.');
        if (cursor.VoiceIndex < 0
            && (separator < 0
                || !int.TryParse(voiceId[(separator + 1)..], out cursor.VoiceIndex)))
            return false;
        // Some rips emit events for a pseudo-voice outside the 8-voice S-DSP
        // range (e.g. a key-off broadcast). Those panels cannot carry a
        // per-voice chip header; fall back to the generic label.
        if (cursor.VoiceIndex < 0 || cursor.VoiceIndex >= SnesDspPresentation.VoiceCount)
            return false;

        if (currentSample < cursor.LastSample)
            cursor.Rewind();
        // Rewind clears the cached voice identity, so restore it after a
        // backwards/random-access query without reparsing every frame.
        if (cursor.VoiceIndex < 0)
        {
            if (separator < 0
                || !int.TryParse(voiceId[(separator + 1)..], out cursor.VoiceIndex))
                return false;
        }

        SpcVoiceStateEvent[] states = panel.SpcVoiceStates;
        while (cursor.VoiceStateIndex < states.Length
            && states[cursor.VoiceStateIndex].SamplePosition <= currentSample)
        {
            SpcVoiceStateEvent state = states[cursor.VoiceStateIndex++];
            if (state.SamplePosition > currentSample)
                break;
            switch (state.State)
            {
                case nameof(SpcSemanticEventKind.SourceLatched):
                    cursor.Source = state.Value;
                    break;
                case nameof(SpcSemanticEventKind.VolumeChanged):
                    cursor.VolumeLeft = unchecked((sbyte)state.Value);
                    cursor.VolumeRight = unchecked((sbyte)state.Value2);
                    break;
                case nameof(SpcSemanticEventKind.NoiseChanged):
                    cursor.Noise = state.Value != 0;
                    break;
                case nameof(SpcSemanticEventKind.PitchModChanged):
                    cursor.PitchMod = state.Value != 0;
                    break;
                case nameof(SpcSemanticEventKind.EchoSendChanged):
                    cursor.EchoSend = state.Value != 0;
                    break;
            }
        }

        SamplePlaybackEvent[] playbackEvents = panel.SamplePlayback;
        while (cursor.PlaybackIndex < playbackEvents.Length
            && playbackEvents[cursor.PlaybackIndex].EndSample <= currentSample)
        {
            cursor.PlaybackIndex++;
            cursor.PlaybackIdentityIndex = -1;
        }

        string shortHash = cursor.ShortHash;
        if (cursor.PlaybackIdentityIndex != cursor.PlaybackIndex)
        {
            shortHash = "";
            if (cursor.PlaybackIndex < playbackEvents.Length)
            {
                SamplePlaybackEvent playback = playbackEvents[cursor.PlaybackIndex];
                if (playback.StartSample <= currentSample && currentSample < playback.EndSample)
                {
                // §13: prefer the established sample display name (e.g.
                // "BRR 02df5") over the raw id suffix.
                if (panel.SamplesById.TryGetValue(playback.SampleId, out SampleDefinition definition)
                    && !string.IsNullOrWhiteSpace(definition.DisplayName))
                {
                    shortHash = definition.DisplayName;
                }
                else
                {
                    int hashSeparator = playback.SampleId.LastIndexOf(':');
                    shortHash = hashSeparator >= 0
                        ? playback.SampleId[(hashSeparator + 1)..]
                        : playback.SampleId;
                }
                }
            }
            cursor.PlaybackIdentityIndex = cursor.PlaybackIndex;
        }

        bool headerDirty = cursor.Header is null
            || cursor.HeaderSource != cursor.Source
            || cursor.HeaderVolumeLeft != cursor.VolumeLeft
            || cursor.HeaderVolumeRight != cursor.VolumeRight
            || cursor.HeaderNoise != cursor.Noise
            || cursor.HeaderPitchMod != cursor.PitchMod
            || cursor.HeaderEchoSend != cursor.EchoSend
            || !string.Equals(cursor.ShortHash, shortHash, StringComparison.Ordinal);
        if (headerDirty)
        {
            SnesDspPanelHeader chipHeader = SnesDspPresentation.BuildHeader(
                cursor.VoiceIndex,
                cursor.Source,
                shortHash,
                cursor.Noise,
                cursor.PitchMod,
                cursor.EchoSend,
                cursor.VolumeLeft,
                cursor.VolumeRight);
            cursor.ShortHash = shortHash;
            cursor.Header = new PanelHeaderData(chipHeader.Label);
            cursor.HeaderSource = cursor.Source;
            cursor.HeaderVolumeLeft = cursor.VolumeLeft;
            cursor.HeaderVolumeRight = cursor.VolumeRight;
            cursor.HeaderNoise = cursor.Noise;
            cursor.HeaderPitchMod = cursor.PitchMod;
            cursor.HeaderEchoSend = cursor.EchoSend;
        }
        cursor.LastSample = currentSample;
        header = cursor.Header;
        return true;
    }
}
