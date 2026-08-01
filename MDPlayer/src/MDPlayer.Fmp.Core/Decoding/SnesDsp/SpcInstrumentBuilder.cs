using Fmp.Core.Playback.Spc;
using Fmp.Core.Visualization;

namespace Fmp.Core.Decoding.SnesDsp;

/// <summary>Aggregated sample record for samples.json (§26.1): one entry per unique sample
/// hash, listing every source number (SRCN) that references that sample content.</summary>
internal sealed record SpcSampleEntry(
    string Hash,
    string ShortHash,
    List<int> SourceNumbers,
    ushort StartAddress,
    ushort LoopAddress,
    bool Loops,
    byte[] EncodedBytes,
    string PitchAccuracy,
    double? EstimatedRootHz,
    double Confidence);

/// <summary>Resolves SPC instruments from a snapshot at accepted key-on: reads DSP DIR and the
/// source directory, walks the BRR chain, and builds an instrument keyed by sample hash +
/// ADSR/GAIN/noise state, cached so the same sample+envelope reuses one instrument.</summary>
internal sealed class SpcInstrumentBuilder
{
    private const int VoiceCount = 8;
    private const int DirRegisterAddress = 0x5D; // DSP DIR register (spec §14)
    private const int NonRegisterAddress = 0x3D; // DSP NON register (spec §14)

    private readonly Dictionary<SpcInstrumentKey, SpcInstrumentDefinition> _instruments = new();
    private readonly Dictionary<string, SpcSampleEntry> _samples = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PitchEstimate> _pitchCache = new(StringComparer.Ordinal);
    private readonly double? _estimatedRootHz;
    private readonly double _rootConfidence;
    private readonly string _pitchAccuracy;
    private readonly bool _enablePitchEstimation;

    public SpcInstrumentBuilder(
        double? estimatedRootHz = null,
        double rootConfidence = 0,
        string pitchAccuracy = "relative",
        bool enablePitchEstimation = false)
    {
        _estimatedRootHz = estimatedRootHz;
        _rootConfidence = rootConfidence;
        _pitchAccuracy = string.IsNullOrWhiteSpace(pitchAccuracy) ? "relative" : pitchAccuracy;
        _enablePitchEstimation = enablePitchEstimation;
    }

    public IReadOnlyCollection<SpcInstrumentDefinition> Instruments => _instruments.Values;

    public IReadOnlyCollection<SpcSampleEntry> Samples =>
        _samples.Values
            .OrderBy(s => s.Hash, StringComparer.Ordinal)
            .Select(s => s with { SourceNumbers = s.SourceNumbers.OrderBy(n => n).ToList() })
            .ToArray();

    public SpcInstrumentDefinition Resolve(SpcSnapshot snapshot, int voice)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (voice < 0 || voice >= VoiceCount)
            throw new ArgumentOutOfRangeException(nameof(voice));

        byte[] ram = snapshot.Ram;
        byte[] dsp = snapshot.DspRegisters;
        // Voice v block at 0x10*v; SRCN/ADSR1/ADSR2/GAIN are the 5th..8th byte of the block.
        TryReadDspRegister(dsp, 0x10 * voice + 0x04, out byte srcn);
        TryReadDspRegister(dsp, 0x10 * voice + 0x05, out byte adsr1);
        TryReadDspRegister(dsp, 0x10 * voice + 0x06, out byte adsr2);
        TryReadDspRegister(dsp, 0x10 * voice + 0x07, out byte gain);
        TryReadDspRegister(dsp, NonRegisterAddress, out byte non);
        bool noise = (non & (1 << voice)) != 0;

        BrrSample sample = ResolveSample(ram, dsp, srcn);
        var key = new SpcInstrumentKey(sample.Hash, adsr1, adsr2, gain, noise);
        if (_instruments.TryGetValue(key, out SpcInstrumentDefinition cached))
            return cached;

        // Opt-in path (PR 9): estimate the musical root when enabled.
        (double? rootHz, double confidence, string accuracy) = _enablePitchEstimation
            ? ToRoot(EstimateCached(sample))
            : (_estimatedRootHz, _rootConfidence, _pitchAccuracy);

        var definition = new SpcInstrumentDefinition(
            BuildInstrumentId(srcn, sample.ShortHash, adsr1, adsr2, gain, noise),
            SpcInstrumentDefinition.DefaultDisplayName(srcn, sample.Hash),
            sample.Hash,
            srcn,
            sample.StartAddress,
            sample.LoopAddress,
            sample.Loops,
            adsr1,
            adsr2,
            gain,
            rootHz,
            confidence,
            accuracy);
        _instruments[key] = definition;
        AddSample(sample, srcn, rootHz, confidence, accuracy);
        return definition;
    }

    /// <summary>Estimate the musical root of a BRR sample (§15.4) and cache it by sample
    /// hash. Returns null Hz with Accuracy 'unpitched'/'relative' for aperiodic or too-short
    /// samples; never labels an estimate as exact.</summary>
    public (double? Hz, double Confidence, string Accuracy) EstimateRoot(BrrSample sample) =>
        ToRoot(EstimateCached(sample));

    /// <summary>
    /// §25.3: resolves every valid directory source — not just the sources the
    /// eight snapshot voices happen to reference — and estimates its BRR root,
    /// so the timeline decoder can anchor notes at sounding pitch even when a
    /// source is first keyed on mid-song. Zero-filled (absent) directory
    /// entries are skipped. Returns an empty list when estimation is disabled
    /// (<see cref="SpcPitchMode.Relative"/>), leaving the decoder at its
    /// relative A4 anchor.
    /// </summary>
    public IReadOnlyList<SpcSourceRootInfo> ResolveSourceRoots(SpcSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_enablePitchEstimation)
            return Array.Empty<SpcSourceRootInfo>();

        var roots = new List<SpcSourceRootInfo>();
        for (int srcn = 0; srcn <= 255; srcn++)
        {
            if (!TryReadDspRegister(snapshot.DspRegisters, DirRegisterAddress, out byte dirReg))
                break;
            ushort dirAddress = (ushort)(dirReg << 8);
            if (!TryReadDirectoryEntry(snapshot.Ram, dirAddress, srcn, out ushort startAddress, out ushort loopAddress))
                continue;
            if (startAddress == 0 && loopAddress == 0)
                continue; // zero-filled directory slot: no sample wired

            BrrSample sample = BrrSampleReader.Read(snapshot.Ram, startAddress, loopAddress);
            if (sample == null || !sample.Valid || sample.EncodedBlocks == null || sample.EncodedBlocks.Length == 0)
                continue;
            (double? hz, double confidence, string accuracy) = EstimateRoot(sample);
            if (hz is > 0)
                roots.Add(new SpcSourceRootInfo(srcn, hz, confidence, accuracy));
        }
        return roots;
    }

    private PitchEstimate EstimateCached(BrrSample sample)
    {
        if (sample == null || !sample.Valid || sample.EncodedBlocks == null || sample.EncodedBlocks.Length == 0)
            return new PitchEstimate(0, 0, PitchEstimateReason.TooShort);
        if (_pitchCache.TryGetValue(sample.Hash, out PitchEstimate cached))
            return cached;
        PitchEstimate estimate = BrrPitchEstimator.Estimate(sample);
        _pitchCache[sample.Hash] = estimate;
        return estimate;
    }

    private static (double? Hz, double Confidence, string Accuracy) ToRoot(PitchEstimate estimate)
    {
        bool pitched = estimate.Reason is not (PitchEstimateReason.Aperiodic or PitchEstimateReason.TooShort)
                       && estimate.FrequencyHz > 0;
        return (pitched ? estimate.FrequencyHz : null, estimate.Confidence, BrrPitchEstimator.AccuracyString(estimate));
    }

    private static BrrSample ResolveSample(byte[] ram, byte[] dsp, int srcn)
    {
        if (ram == null || ram.Length == 0 || !TryReadDspRegister(dsp, DirRegisterAddress, out byte dirReg))
            return InvalidSample();
        ushort dirAddress = (ushort)(dirReg << 8); // RAM address = DIR << 8
        if (!TryReadDirectoryEntry(ram, dirAddress, srcn, out ushort startAddress, out ushort loopAddress))
            return InvalidSample();
        return BrrSampleReader.Read(ram, startAddress, loopAddress);
    }

    private static bool TryReadDspRegister(byte[] dsp, int address, out byte value)
    {
        value = 0;
        if (dsp == null || address < 0 || address >= dsp.Length)
            return false;
        value = dsp[address];
        return true;
    }

    private static bool TryReadDirectoryEntry(byte[] ram, ushort dirAddress, int srcn, out ushort startAddress, out ushort loopAddress)
    {
        startAddress = 0;
        loopAddress = 0;
        if (srcn < 0 || srcn > 255 || ram == null || ram.Length < 0x10000)
            return false;
        int offset = dirAddress + 4 * srcn; // entry: start(2) loop(2) unused(2)
        if (offset + 3 > 0xFFFF) return false; // entry would cross the 64 KiB boundary: never trust it
        startAddress = (ushort)(ram[offset] | (ram[offset + 1] << 8));
        loopAddress = (ushort)(ram[offset + 2] | (ram[offset + 3] << 8));
        return true;
    }

    private static BrrSample InvalidSample() =>
        new(0, 0, Array.Empty<byte>(), false, false, BrrSampleReader.Sha256Hex(ReadOnlySpan<byte>.Empty));

    private static string BuildInstrumentId(int sourceNumber, string shortHash, byte adsr1, byte adsr2, byte gain, bool noise) =>
        $"spc:src{sourceNumber}:{shortHash}:{adsr1:x2}{adsr2:x2}{gain:x2}{(noise ? "n" : "")}";

    private void AddSample(BrrSample sample, int sourceNumber, double? rootHz, double confidence, string accuracy)
    {
        if (_samples.TryGetValue(sample.Hash, out SpcSampleEntry entry))
        {
            if (!entry.SourceNumbers.Contains(sourceNumber))
                entry.SourceNumbers.Add(sourceNumber);
            return;
        }
        var sources = new List<int> { sourceNumber };
        _samples[sample.Hash] = new SpcSampleEntry(
            sample.Hash,
            sample.ShortHash,
            sources,
            sample.StartAddress,
            sample.LoopAddress,
            sample.Loops,
            sample.EncodedBlocks,
            accuracy,
            rootHz,
            confidence);
    }
}
