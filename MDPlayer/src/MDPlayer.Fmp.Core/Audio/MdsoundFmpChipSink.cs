using System.Reflection;
using Fmp.Core.Audio;

namespace Fmp.Core.Audio.Mdsound;

/// <summary>
/// IFmpChipSink implementation that drives MDSound YM2608 and PPZ8 synthesis.
/// Register writes are routed directly to the chip instances (ym2608.Write /
/// ppz8.Write). With the fixed MDSound.dll (kuma4649/MDSound build be9a73f4),
/// both ym2608.Write and MDSound.WriteYM2608 propagate register state correctly,
/// including rhythm keyon (SetReg 0x10 resets rhythm pos and RhythmMix produces
/// output). Direct chip writes are used for consistency with PPZ8.
/// Rhythm samples are loaded from embedded resources (2608_*.wav).
/// </summary>
internal class MdsoundFmpChipSink : IFmpChipSink, IDisposable
{
    private const uint ChipClock = 7987200;

    private readonly MDSound.MDSound _mds;
    private readonly MDSound.ym2608 _ym2608;
    private readonly MDSound.PPZ8 _ppz8;
    private readonly MDSound.MDSound.Chip _ym2608Chip;
    private readonly MDSound.MDSound.Chip _ppz8Chip;
    private readonly object[] _ymOption;
    private bool _started;
    private byte _chipId;
    private readonly int _sampleRate;

    public MdsoundFmpChipSink(int sampleRate = 44100, byte chipId = 0, double ssgGainDb = 0)
    {
        _chipId = chipId;
        _sampleRate = sampleRate;

        _ym2608 = new MDSound.ym2608();
        _ppz8 = new MDSound.PPZ8();

        // YM2608 option: function to load rhythm samples from embedded resources
        _ymOption = new object[] { (Func<string, Stream>)GetRhythmStream };

        // Create MDSound chip definitions.
        // YM2608 SamplingRate matches the original MDPlayer (55467); MDSound resamples to output rate.
        _ym2608Chip = new MDSound.MDSound.Chip
        {
            ID = 0,
            type = MDSound.MDSound.enmInstrumentType.YM2608,
            Instrument = _ym2608,
            Update = _ym2608.Update,
            Start = _ym2608.Start,
            Stop = _ym2608.Stop,
            Reset = _ym2608.Reset,
            Clock = ChipClock,
            SamplingRate = 55467,
            Volume = 0,
            Option = _ymOption
        };

        _ppz8Chip = new MDSound.MDSound.Chip
        {
            ID = 0,
            type = MDSound.MDSound.enmInstrumentType.PPZ8,
            Instrument = _ppz8,
            Update = _ppz8.Update,
            Start = _ppz8.Start,
            Stop = _ppz8.Stop,
            Reset = _ppz8.Reset,
            Clock = ChipClock,
            SamplingRate = (uint)sampleRate,
            Volume = 0,
            Option = null
        };

        // Initialize MDSound with output sample rate
        _mds = new MDSound.MDSound((uint)sampleRate, 1024, new[] { _ym2608Chip, _ppz8Chip });

        // Default volumes: all chip groups at normal volume (0 dB).
        // Rhythm samples are now supplied from embedded resources, so the rhythm
        // channel is no longer silenced. ADPCM has no ROM data in the FMP path
        // and stays silent by chip default; PPZ8 is driven by loaded banks.
        // The SSG/PSG group applies the user-facing --ssg-gain-db mix setting.
        _mds.SetVolumeYM2608(0);
        _mds.SetVolumeYM2608FM(0);
        _mds.SetVolumeYM2608PSG(ToMdsoundVolume(ssgGainDb));
        _mds.SetVolumeYM2608Rhythm(0);
        _mds.SetVolumeYM2608Adpcm(0);
        _mds.SetVolumePPZ8(0);
    }

    /// <summary>
    /// Convert a decibel gain to MDSound's volume units.
    /// MDSound (fmgen) applies volume as <c>scale * Math.Pow(10, vol / 40)</c>,
    /// i.e. the parameter is in units of 0.5 dB. One decibel therefore equals
    /// two MDSound units. Verified against the MDSound source (kuma4649/MDSound):
    /// <c>fmgen/psg.cs</c> <c>SetVolume</c> uses
    /// <c>0x4000/3.0 * Math.Pow(10.0, volume / 40.0)</c>, reached via
    /// <c>ym2608.SetPSGVolume</c> → <c>opna.SetVolumePSG</c> → <c>psg.SetVolume</c>.
    /// The PSG path has no internal clamp; the CLI validates a [-60, +12] dB
    /// range (→ [-120, +24] MDSound units) before this conversion.
    /// </summary>
    internal static int ToMdsoundVolume(double gainDb) => checked((int)Math.Round(gainDb * 2.0));

    /// <summary>
    /// Start the chips.
    /// MDSound's constructor already started and reset every registered chip
    /// with the correct (clock, sampling rate) order, so this method only
    /// applies the same post-initialization writes the original MDPlayer uses
    /// before booting the FMP driver.
    /// </summary>
    public void Start()
    {
        if (_started) return;

        // The original OxiPlay_FMP path writes these registers to the virtual
        // YM2608 before the FMP driver is initialized. Reproduce them here so
        // the chip starts from the same state.
        _ym2608.Write(_chipId, 0, 0x2d, 0x00);
        _ym2608.Write(_chipId, 0, 0x29, 0x82);
        _ym2608.Write(_chipId, 0, 0x07, 0x38);

        _started = true;
    }

    /// <summary>
    /// Stop the chips.
    /// </summary>
    public void Stop()
    {
        if (!_started) return;
        _ym2608.Stop(_chipId);
        _ppz8.Stop(_chipId);
        _started = false;
    }

    /// <summary>
    /// Reset the chips to initial state.
    /// </summary>
    public void Reset()
    {
        Stop();
        Start();
    }

    private short[]? _renderBuf;

    /// <summary>
    /// Render one buffer of stereo PCM output.
    /// MDSound handles resampling and mixing of the registered chips.
    /// <paramref name="frameCallback"/> is invoked once per output stereo sample
    /// by MDSound.Update, interleaving emulation ticks with sample rendering —
    /// matching the original MDPlayer's oneFrameProc callback pattern.
    /// </summary>
    public int Render(int[][] outputs, int samples, Action frameCallback = null)
    {
        if (!_started) return samples;

        if (_renderBuf == null || _renderBuf.Length < samples * 2)
            _renderBuf = new short[samples * 2];

        // MDSound.Update sampleCount is the total short count (frames * 2 for
        // stereo), not the frame count. Passing frames (not shorts) renders only
        // half the buffer, leaving the rest stale — this caused a 2x RMS drop.
        // frameCallback is invoked once per output stereo sample, interleaving
        // emulation ticks with rendering (matching original MDPlayer's oneFrameProc).
        _mds.Update(_renderBuf, 0, samples * 2, frameCallback);
        for (int i = 0; i < samples; i++)
        {
            outputs[0][i] = _renderBuf[i * 2];
            outputs[1][i] = _renderBuf[i * 2 + 1];
        }

        return samples;
    }

    public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition)
    {
        if (!_started) return;
        // Route writes through MDSound.WriteYM2608, matching the original
        // Windows MDPlayer's ChipRegister.cs behavior. MDSound.WriteYM2608
        // internally calls ym2608.Write with combined port*256+adr encoding.
        // Do NOT call _ym2608.Write directly — the separate-port direct call
        // uses the wrong register addressing convention, producing incorrect audio.
        _mds.WriteYM2608((byte)chipId, (byte)port, (byte)address, (byte)value);
    }

    public void LoadYm2608AdpcmData(uint startAddress, byte[] data)
    {
        if (!_started)
            return;
        ArgumentNullException.ThrowIfNull(data);

        // YM2608 ADPCM RAM transfer, matching MDPlayer's VGM data-block path
        // for block type 0x81. The portable MDSound wrapper exposes the same
        // register interface as the hardware rather than a direct RAM API.
        WriteYm2608(0, 1, 0x00, 0x20, 0);
        WriteYm2608(0, 1, 0x00, 0x21, 0);
        WriteYm2608(0, 1, 0x00, 0x00, 0);
        WriteYm2608(0, 1, 0x10, 0x00, 0);
        WriteYm2608(0, 1, 0x10, 0x80, 0);
        WriteYm2608(0, 1, 0x00, 0x61, 0);
        WriteYm2608(0, 1, 0x00, 0x68, 0);
        WriteYm2608(0, 1, 0x01, 0x00, 0);
        WriteYm2608(0, 1, 0x02, (int)((startAddress >> 2) & 0xFF), 0);
        WriteYm2608(0, 1, 0x03, (int)((startAddress >> 10) & 0xFF), 0);
        WriteYm2608(0, 1, 0x04, 0xFF, 0);
        WriteYm2608(0, 1, 0x05, 0xFF, 0);
        WriteYm2608(0, 1, 0x0C, 0xFF, 0);
        WriteYm2608(0, 1, 0x0D, 0xFF, 0);
        foreach (byte value in data)
            WriteYm2608(0, 1, 0x08, value, 0);
        WriteYm2608(0, 1, 0x00, 0x00, 0);
        WriteYm2608(0, 1, 0x10, 0x80, 0);
    }

    public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition)
    {
        if (!_started) return;
        var pcmData = new byte[samples.Length][];
        for (int i = 0; i < samples.Length; i++)
            pcmData[i] = samples[i].ToArray();
        // Use MDSound.WritePPZ8PCMData to match Windows MDPlayer behavior
        _mds.WritePPZ8PCMData(_chipId, bank, mode, pcmData);
    }

    public void WritePpz8(int port, int address, int value, long samplePosition)
    {
        if (!_started) return;
        // Use MDSound.WritePPZ8 to match Windows MDPlayer behavior
        _mds.WritePPZ8(_chipId, port, address, value, null);
    }

    public void Dispose()
    {
        Stop();
    }

    /// <summary>
    /// Set the volume for a chip group (SSG/PSG, Rhythm, ADPCM, or PPZ8).
    /// Used to apply the user-facing SSG mix gain and by <see cref="MaskedChipSink"/>
    /// to silence non-FM/SSG groups. <paramref name="volume"/> is in MDSound's
    /// 0.5-dB units (0 = normal, -192 = silent); callers should obtain it via
    /// <see cref="ToMdsoundVolume"/>.
    /// </summary>
    /// <param name="group">The volume group to adjust.</param>
    /// <param name="volume">Volume in dB-scaled MDSound units (0 = normal, -192 = silent).</param>
    public void SetVolume(VolumeGroup group, int volume)
    {
        switch (group)
        {
            case VolumeGroup.Ssg:
                _mds.SetVolumeYM2608PSG(volume);
                break;
            case VolumeGroup.Rhythm:
                _mds.SetVolumeYM2608Rhythm(volume);
                break;
            case VolumeGroup.Adpcm:
                _mds.SetVolumeYM2608Adpcm(volume);
                break;
            case VolumeGroup.Ppz8:
                _mds.SetVolumePPZ8(volume);
                break;
        }
    }

    private static readonly Dictionary<string, string> RhythmResourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2608_BD.WAV"]  = "Fmp.Core.Resources.2608_bd.wav",
        ["2608_HH.WAV"]  = "Fmp.Core.Resources.2608_hh.wav",
        ["2608_RIM.WAV"] = "Fmp.Core.Resources.2608_rim.wav",
        ["2608_RYM.WAV"] = "Fmp.Core.Resources.2608_rym.wav",
        ["2608_SD.WAV"]  = "Fmp.Core.Resources.2608_sd.wav",
        ["2608_TOM.WAV"] = "Fmp.Core.Resources.2608_tom.wav",
        ["2608_TOP.WAV"] = "Fmp.Core.Resources.2608_top.wav",
    };

    /// <summary>
    /// Rhythm sample loader for MDSound YM2608. Loads the requested sample from
    /// embedded resources (2608_*.wav). Returns null if not found, leaving that
    /// rhythm channel silent.
    /// MDSound requests samples by names like "2608_BD_0.WAV" (with a "_0"
    /// suffix for the first chip); the suffix is stripped before lookup.
    /// </summary>
    private static Stream GetRhythmStream(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        // Normalize: uppercase, strip optional "_0" suffix before extension.
        var upper = name.ToUpperInvariant();
        if (upper.EndsWith("_0.WAV", StringComparison.Ordinal))
            upper = upper[..^6] + ".WAV";

        if (!RhythmResourceNames.TryGetValue(upper, out var resourceName)) return null;

        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        // MDSound reads the WAV header itself, so return the full file stream.
        return stream;
    }
}
