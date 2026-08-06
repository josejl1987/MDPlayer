using System.Security.Cryptography;
using Fmp.Core.PlaybackAssets.Furnace;
using Fmp.Core.PlaybackAssets.Opn;
using Fmp.Core.Visualization;

namespace Fmp.Core.PlaybackAssets;

/// <summary>
/// Default collection engine. Owns one <see cref="OpnFmRegisterTracker"/> per
/// physical OPN chip instance, decodes key-on writes into canonical instrument
/// snapshots, serializes their normalized timbre identity and deduplicates by
/// the SHA-256 of those identity bytes while additionally recording the
/// observation metadata used by the manifest.
///
/// Deduplication is byte identity of the normalized timbre identity, not the
/// literal 42-byte TFI: two patches that differ only by a uniform carrier-level
/// offset (a common way drivers implement channel volume) are one asset, while
/// carrier balance, every modulator TL and all non-TL parameters remain
/// significant. The exported file is a canonicalized TFI with the loudest
/// carrier at TL=0.
/// </summary>
internal sealed class PlaybackAssetCollector : IPlaybackAssetCollector
{
    private readonly Dictionary<ChipKey, OpnFmRegisterTracker> _trackers = new();
    private readonly Dictionary<string, CapturedFmInstrument> _byIdentityHash = new();
    private readonly List<CapturedFmInstrument> _ordered = new();

    public void Observe(in ChipWriteEvent write)
    {
        if (!OpnChipCapabilities.TryGetCapabilities(write.ChipType, out var capabilities))
        {
            // Chip type is not an OPN FM chip this pipe understands; ignore.
            return;
        }

        OpnFmRegisterTracker tracker = TrackerFor(write.ChipType, write.ChipIndex, capabilities);

        OpnFmInstrument? snapshot = tracker.HandleWrite(write.Port, write.Address, write.Data);
        if (snapshot == null)
            return;

        Record(write, snapshot);
    }

    public PlaybackAssetSnapshot Complete()
    {
        return new PlaybackAssetSnapshot(_ordered);
    }

    private OpnFmRegisterTracker TrackerFor(
        ChipType chipType, int chipIndex, OpnChipCapabilities capabilities)
    {
        var key = new ChipKey(chipType, chipIndex);
        if (!_trackers.TryGetValue(key, out var tracker))
        {
            tracker = new OpnFmRegisterTracker(capabilities);
            _trackers.Add(key, tracker);
        }
        return tracker;
    }

    private void Record(
        in ChipWriteEvent write,
        OpnFmInstrument snapshot)
    {
        // The normalized timbre identity is the deduplication key; the
        // canonicalized TFI is what gets exported.
        byte[] identityBytes = OpnFmTimbreIdentityWriter.Write(snapshot);
        string identityHash = ComputeSha256Hex(identityBytes);

        OpnFmInstrument representative =
            OpnFmAlgorithm.Canonicalize(snapshot);
        byte[] canonicalTfi = TfiInstrumentWriter.Write(representative);
        string canonicalSha = ComputeSha256Hex(canonicalTfi);

        byte volumeOffset =
            OpnFmAlgorithm.GetVolumeOffset(snapshot);

        if (!_byIdentityHash.TryGetValue(identityHash, out var captured))
        {
            captured = new CapturedFmInstrument
            {
                Ordinal = _ordered.Count + 1,
                IdentityHash = identityHash,
                Representative = representative,
                Sha256Hex = canonicalSha,
                TfiBytes = canonicalTfi
            };
            _byIdentityHash.Add(identityHash, captured);
            _ordered.Add(captured);
        }

        captured.ObservationCount++;
        captured.ChipTypesObserved.Add(write.ChipType);
        captured.ChipInstancesObserved.Add(write.ChipIndex);

        int channel = DecodeCapturedChannel(write);
        if (channel >= 0)
            captured.ChannelsObserved.Add(channel);

        if (captured.FirstSeenWriteIndex < 0 && write.WriteIndex.HasValue)
            captured.FirstSeenWriteIndex = write.WriteIndex.Value;

        captured.FirstSeenPlaybackSample ??= write.PlaybackSample;

        RecordVolumeOffset(captured, snapshot, volumeOffset);
    }

    private static void RecordVolumeOffset(
        CapturedFmInstrument captured,
        OpnFmInstrument snapshot,
        byte volumeOffset)
    {
        captured.FeedbackCarrierPreserved =
            snapshot.Algorithm == 7 && snapshot.Feedback != 0;

        captured.MinimumObservedVolumeOffset =
            Math.Min(captured.MinimumObservedVolumeOffset, volumeOffset);
        captured.MaximumObservedVolumeOffset =
            Math.Max(captured.MaximumObservedVolumeOffset, volumeOffset);

        captured.CarrierLevelVariants.Add(new CarrierLevelVariant(volumeOffset));
    }

    private static int DecodeCapturedChannel(in ChipWriteEvent write)
    {
        // The key-on write encodes the global channel in its low three bits
        // (0..2 primary, 4..6 on six-channel chips). The channel recorded in
        // the manifest is the global zero-based FM channel.
        return (write.Data & 0x07) switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            4 => 3,
            5 => 4,
            6 => 5,
            _ => -1
        };
    }

    private static string ComputeSha256Hex(byte[] data)
    {
        byte[] digest = SHA256.HashData(data);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private readonly record struct ChipKey(ChipType Type, int Instance);
}
