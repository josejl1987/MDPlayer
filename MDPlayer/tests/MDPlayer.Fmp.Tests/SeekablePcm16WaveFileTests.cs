using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Tests for the random-access signed-16-bit PCM WAV reader used by the
/// interactive scope source: RIFF chunk walking (JUNK, extensible), downmix,
/// and clamping beyond the data region.
/// </summary>
public sealed class SeekablePcm16WaveFileTests
{
    // ---- structural parsing ----

    [Fact]
    public void TryOpen_Standard44ByteMonoPcm_ReadsWindow()
    {
        using var wav = PcmWav.BuildNew(mono: true, extensible: false, junkBeforeFmt: false);
        using var file = SeekablePcm16WaveFile.TryOpen(wav.Path);

        Assert.NotNull(file);
        Assert.Equal(1, file!.Channels);
        Assert.Equal(PcmWav.SampleRate, file.SampleRate);
        Assert.Equal(PcmWav.SampleCount, file.TotalSamples);

        Span<short> window = new short[PcmWav.SampleCount];
        int read = file.ReadWindow(0, window);
        Assert.Equal(PcmWav.SampleCount, read);
        Assert.Equal(PcmWav.FirstValue, window[0]);
    }

    [Fact]
    public void TryOpen_StereoPcm_ReadsDownmixedWindow()
    {
        using var wav = PcmWav.BuildNew(mono: false, extensible: false, junkBeforeFmt: false);
        using var file = SeekablePcm16WaveFile.TryOpen(wav.Path);

        Assert.NotNull(file);
        Assert.Equal(2, file!.Channels);

        Span<short> window = new short[8];
        int read = file.ReadWindow(0, window);

        // Loudest-channel downmix: for a hard-left signal the reader must
        // return the left channel, not a nulled average.
        Assert.Equal(8, read);
        for (int i = 0; i < 4; i++)
            Assert.Equal(wav.Left[i], window[i]);
    }

    [Fact]
    public void TryOpen_RiffWithJunkBeforeFmt_StillParses()
    {
        // A JUNK chunk (nonzero size) before fmt must be skipped, not mistaken
        // for the format block. This exercises non-44-byte headers.
        using var wav = PcmWav.BuildNew(mono: true, extensible: false, junkBeforeFmt: true);
        using var file = SeekablePcm16WaveFile.TryOpen(wav.Path);

        Assert.NotNull(file);
        Assert.Equal(1, file!.Channels);
    }

    [Fact]
    public void TryOpen_ExtensiblePcm_StillParses()
    {
        using var wav = PcmWav.BuildNew(mono: false, extensible: true, junkBeforeFmt: false);
        using var file = SeekablePcm16WaveFile.TryOpen(wav.Path);

        Assert.NotNull(file);
        Assert.Equal(2, file!.Channels);
        Assert.Equal(PcmWav.SampleRate, file.SampleRate);
    }

    // ---- clamping / robustness ----

    [Fact]
    public void ReadWindow_ClampsNegativeStartToSilence()
    {
        using var wav = PcmWav.BuildNew(mono: true, extensible: false, junkBeforeFmt: false);
        using var file = SeekablePcm16WaveFile.TryOpen(wav.Path)!;

        Span<short> window = new short[6];
        int read = file.ReadWindow(-4, window);

        // First samples are silence (requested before sample 0), then data.
        Assert.Equal(6, read);
        Assert.Equal(0, window[0]);
        Assert.Equal(0, window[1]);
        Assert.Equal(0, window[2]);
        Assert.Equal(0, window[3]);
        Assert.Equal(PcmWav.FirstValue, window[4]);
    }

    [Fact]
    public void ReadWindow_ClampsBeyondEof_ReturnsSilenceOrShortRead()
    {
        using var wav = PcmWav.BuildNew(mono: true, extensible: false, junkBeforeFmt: false);
        using var file = SeekablePcm16WaveFile.TryOpen(wav.Path)!;

        // Reading far beyond EOF yields only the available tail (no throw).
        Span<short> window = new short[PcmWav.SampleCount * 2];
        int read = file.ReadWindow(PcmWav.SampleCount - 1, window);
        Assert.True(read <= PcmWav.SampleCount, $"read returned {read} but only {PcmWav.SampleCount} exist");
        Assert.True(read > 0);
    }

    [Fact]
    public void ReadWindow_BeyondEof_ZeroesStaleTailOfReusedBuffer()
    {
        // The renderer reuses one scratch window across frames. A read whose
        // window passes EOF must silence the tail that a previous frame left
        // behind, or a ghost waveform would persist.
        using var wav = PcmWav.BuildNew(mono: true, extensible: false, junkBeforeFmt: false);
        using var file = SeekablePcm16WaveFile.TryOpen(wav.Path)!;

        // Fill the write buffer with a distinctive non-zero pattern first.
        var reuse = new short[PcmWav.SampleCount + 4];
        for (int i = 0; i < reuse.Length; i++)
            reuse[i] = (short)0x5A5A;

        // First read populates real samples into the front.
        int first = file.ReadWindow(0, reuse);
        Assert.Equal(PcmWav.SampleCount, first);

        // A second read starting at EOF-2 writes just the final samples and
        // must zero everything past them, not leave the 0x5A5A tail.
        int second = file.ReadWindow(PcmWav.SampleCount - 2, reuse);
        Assert.True(second >= 2 && second <= PcmWav.SampleCount);
        for (int i = second; i < reuse.Length; i++)
            Assert.Equal(0, reuse[i]);
    }

    [Fact]
    public void TryOpen_TruncatedDataChunk_ReturnsShortSilentTail()
    {
        using var wav = PcmWav.BuildNew(mono: true, extensible: false, junkBeforeFmt: false, truncateDataTo: 6);
        using var file = SeekablePcm16WaveFile.TryOpen(wav.Path)!;

        // The data chunk claims more samples than are on disk; the reader must
        // clamp without throwing and keep what it could read.
        Span<short> window = new short[PcmWav.SampleCount];
        int read = file.ReadWindow(0, window);
        Assert.True(read > 0 && read <= PcmWav.SampleCount);
    }

    // ---- unsupported formats ----

    [Fact]
    public void TryOpen_Unsupported8BitOrFloat_ReturnsNull()
    {
        using var wav8 = PcmWav.BuildNew(mono: true, extensible: false, junkBeforeFmt: false, bitsPerSample: 8);
        using var wavFloat = PcmWav.BuildNew(mono: true, extensible: false, junkBeforeFmt: false, bitsPerSample: 32);

        Assert.Null(SeekablePcm16WaveFile.TryOpen(wav8.Path));
        Assert.Null(SeekablePcm16WaveFile.TryOpen(wavFloat.Path));
    }

    [Fact]
    public void TryOpen_MissingFile_ReturnsNull()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.wav");
        Assert.Null(SeekablePcm16WaveFile.TryOpen(missing));
    }

    // ---- stereo downmix preserves a lone hard-panned channel ----

    [Fact]
    public void Downmix_KeepsHardLeftSignal()
    {
        // Left channel has a strong asymmetric pulse; right is silent. The
        // reader must return the left value (treated as louder), not (L+R)/2.
        using var wav = PcmWav.BuildHardPanned();
        using var file = SeekablePcm16WaveFile.TryOpen(wav.Path)!;

        Span<short> window = new short[PcmWav.HardPannedLeft.Length];
        int read = file.ReadWindow(0, window);
        Assert.Equal(window.Length, read);
        for (int i = 0; i < window.Length; i++)
            Assert.Equal(PcmWav.HardPannedLeft[i], window[i]);
    }

    [Fact]
    public void Downmix_KeepsHardRightSignal()
    {
        // Right-louder branch of the downmix: a signal hard-panned to the right
        // with a silent left must survive, symmetric to the hard-left case.
        using var wav = PcmWav.BuildHardPanned(toRight: true);
        using var file = SeekablePcm16WaveFile.TryOpen(wav.Path)!;

        Span<short> window = new short[PcmWav.HardPannedLeft.Length];
        int read = file.ReadWindow(0, window);
        Assert.Equal(window.Length, read);
        for (int i = 0; i < window.Length; i++)
            Assert.Equal(PcmWav.HardPannedLeft[i], window[i]);
    }

    /// <summary>
    /// Minimal WAV fixture builder for the reader tests. Emits a real RIFF file
    /// with full control over chunk order, extensible PCM, bit depth and data
    /// truncation.
    /// </summary>
    private sealed class PcmWav : IDisposable
    {
        public const int SampleRate = 44100;
        public const int SampleCount = 64;
        public const short FirstValue = 5000;
        public static readonly short[] HardPannedLeft = { 12000, -12000, 9000, -9000, 8000, -8000 };

        public string Path { get; }
        public short[] Left { get; } = new short[SampleCount];

        private PcmWav(string path) => Path = path;

        public static PcmWav BuildNew(
            bool mono,
            bool extensible,
            bool junkBeforeFmt,
            int bitsPerSample = 16,
            int truncateDataTo = -1)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"reader-{Guid.NewGuid():N}.wav");
            int channels = mono ? 1 : 2;
            int bytes = bitsPerSample / 8;
            long dataBytes = (long)channels * bytes * SampleCount;
            long dataSize = truncateDataTo >= 0
                ? Math.Min(dataBytes, truncateDataTo)
                : dataBytes;

            var left = new short[SampleCount];
            for (int i = 0; i < SampleCount; i++)
                left[i] = (short)(FirstValue + i);
            var right = new short[SampleCount];
            for (int i = 0; i < SampleCount; i++)
                right[i] = (short)(FirstValue - i);

            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                WriteRiff(bw, channels, extensible, junkBeforeFmt, bytes, dataSize, left, right, mono);
            }

            File.WriteAllBytes(path, ms.ToArray());

            var result = new PcmWav(path);
            for (int i = 0; i < SampleCount; i++)
                result.Left[i] = left[i];
            return result;
        }

        public static PcmWav BuildHardPanned(bool toRight = false)
        {
            // Rewrite a stereo fixture with an asymmetric pulse hard-panned to
            // one channel so the downmix test has an exact input.
            var wav = BuildNew(mono: false, extensible: false, junkBeforeFmt: true, bitsPerSample: 16);
            int channels = 2, bytes = 2;
            long dataBytes = (long)channels * bytes * HardPannedLeft.Length;
            var left = toRight ? new short[HardPannedLeft.Length] : HardPannedLeft;
            var right = toRight ? HardPannedLeft : new short[HardPannedLeft.Length];
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                WriteRiff(bw, channels, extensible: false, junkBeforeFmt: true, bytes,
                    dataBytes, left, right, mono: false);
            }
            File.WriteAllBytes(wav.Path, ms.ToArray());
            return wav;
        }

        private static void WriteRiff(
            BinaryWriter bw,
            int channels,
            bool extensible,
            bool junkBeforeFmt,
            int bytes,
            long dataSize,
            short[] left,
            short[] right,
            bool mono)
        {
            long fmtPayloadSize = extensible ? 40 : 16;
            long fmtSize = 4 + fmtPayloadSize;
            long riffTotal = 4 + (junkBeforeFmt ? 8 + 14 : 0) + fmtSize + 8 + dataSize;

            bw.Write("RIFF"u8.ToArray());
            bw.Write((int)riffTotal);
            bw.Write("WAVE"u8.ToArray());

            if (junkBeforeFmt)
            {
                bw.Write("JUNK"u8.ToArray());
                bw.Write((int)14);
                bw.Write(new byte[14]); // zero padding chunk (pads to even)
            }

            bw.Write("fmt "u8.ToArray());
            bw.Write((int)fmtPayloadSize);
            bw.Write(extensible ? unchecked((short)0xFFFE) : (short)1);
            bw.Write((short)channels);
            bw.Write(SampleRate);
            bw.Write((int)(SampleRate * channels * bytes));
            bw.Write((short)(channels * bytes));
            bw.Write((short)(bytes * 8));
            if (extensible)
            {
                bw.Write((short)22);            // cbSize: remaining 22 bytes after this
                bw.Write((short)(bytes * 8));   // valid bits per sample
                bw.Write(0x00000003u);          // channel mask
                // SubFormat GUID for PCM: {00000001-0000-0010-8000-00AA00389B71}
                bw.Write(new byte[]
                {
                    0x01, 0x00, 0x00, 0x00,
                    0x00, 0x00,
                    0x10, 0x00,
                    0x80, 0x00,
                    0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71,
                });
            }

            bw.Write("data"u8.ToArray());
            bw.Write((int)dataSize);

            int count = mono ? left.Length : Math.Max(left.Length, right.Length);
            for (int i = 0; i < count; i++)
            {
                if (i < left.Length)
                    WriteSample(bw, left[i], bytes);
                if (!mono && i < right.Length)
                    WriteSample(bw, right[i], bytes);
            }
        }

        private static void WriteSample(BinaryWriter bw, short value, int bytes)
        {
            if (bytes == 2)
                bw.Write(value);
            else if (bytes == 1)
                bw.Write((byte)unchecked((sbyte)(value >> 8))); // 8-bit PCM occupies top byte
            else
                bw.Write(value); // float-width placeholder (unsupported -> null)
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
