using System.Text;
using Fmp.Core.Playback.Spc;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.Playback.Spc;

public sealed class SpcPlaybackBackendTests
{
    [Fact]
    public void Probe_RecognizesValidSpcBySignature()
    {
        using var f = SpcTempFile.Create(SpcFixture.Build());
        var probe = Probe(f);
        Assert.True(probe.Supported);
        Assert.Equal("SPC", probe.Format);
        Assert.True(probe.Visualizable);
        Assert.Equal(PlaybackAvailability.PlatformSpecific, probe.Availability);
        Assert.False(probe.Portable);
    }

    [Fact]
    public void Probe_RejectsTruncatedFile()
    {
        using var f = SpcTempFile.Create(SpcFixture.Build(SpcMetadata.MinimumFileSize - 1));
        var probe = Probe(f);
        Assert.False(probe.Supported);
        Assert.Contains(probe.Warnings, w => w.Contains("too small", StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_RejectsInvalidSignature()
    {
        byte[] data = SpcFixture.Build();
        data[0] = (byte)'X';
        using var f = SpcTempFile.Create(data);
        var probe = Probe(f);
        Assert.False(probe.Supported);
        Assert.Contains(probe.Warnings, w => w.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Probe_RejectsExtensionWithInvalidHeader()
    {
        using var f = SpcTempFile.Create(new byte[0x2_0000]);
        Assert.False(Probe(f).Supported);
    }

    [Fact]
    public void Metadata_DecodesAsciiFields()
    {
        var m = SpcFixture.BuildMetadata(songTitle: "Test Song", gameTitle: "Test Game",
            artist: "Test Artist", dumper: "dumper1", playLengthSeconds: 190, fadeLengthMilliseconds: 8000);
        Assert.Equal("Test Song", m.SongTitle);
        Assert.Equal("Test Game", m.GameTitle);
        Assert.Equal("Test Artist", m.Artist);
        Assert.Equal("dumper1", m.Dumper);
        Assert.Equal(190, m.PlayLengthSeconds);
        Assert.Equal(8000, m.FadeLengthMilliseconds);
    }

    [Fact]
    public void Metadata_DecodesCp932AndUtf8Titles()
    {
        var cp = SpcFixture.BuildMetadata(songTitleBytes: Encoding.GetEncoding(932).GetBytes("曲目"));
        Assert.Equal("曲目", cp.SongTitle);
        var u8 = SpcFixture.BuildMetadata(songTitleBytes: Encoding.UTF8.GetBytes("日本語タイトル"));
        Assert.Equal("日本語タイトル", u8.SongTitle);
    }

    [Fact]
    public void DurationResolver_PrefersExplicitThenMetadataThenDefault()
    {
        var tagged = SpcFixture.BuildMetadata(playLengthSeconds: 180, fadeLengthMilliseconds: 10000);
        Assert.Equal(90, SpcDurationResolver.Resolve(90, 5, tagged).DurationSeconds);
        Assert.Equal("explicit", SpcDurationResolver.Resolve(90, 5, tagged).Source);
        Assert.Equal(180, SpcDurationResolver.Resolve(null, null, tagged).DurationSeconds);
        Assert.Equal("spc-metadata", SpcDurationResolver.Resolve(null, null, tagged).Source);
        var untagged = SpcFixture.BuildMetadata();
        Assert.Equal(SpcDurationResolver.DefaultUntaggedDurationSeconds, SpcDurationResolver.Resolve(null, null, untagged).DurationSeconds);
        Assert.Equal("default", SpcDurationResolver.Resolve(null, null, untagged).Source);
    }

    [Fact]
    public void DurationResolver_ClampsToSafetyMaximum()
    {
        var m = SpcFixture.BuildMetadata(playLengthSeconds: 9999);
        Assert.Equal(SpcDurationResolver.MaximumWithoutOverrideSeconds, SpcDurationResolver.Resolve(null, null, m).DurationSeconds);
    }

    [Fact]
    public void BackendRegistry_DispatchesSpcThroughProbe()
    {
        using var f = SpcTempFile.Create(SpcFixture.Build());
        var env = new PlaybackEnvironment([Path.GetDirectoryName(f.Path)!]);
        var registry = PlaybackBackendRegistry.CreateDefault(env);
        bool selected = registry.TrySelect(new FileInfo(f.Path), env, out var backend, out var probe);
        Assert.True(selected);
        Assert.Equal("spc", backend.Id);
        Assert.True(probe.Supported);
    }

    [Fact]
    public void Open_RendersNativeSessionSincePr2()
    {
        // PR 2 replaced the PR 1 stub: Open now renders through the native
        // backend when libmdplayer_spc.so is available (packaged into the test
        // bin). The actionable missing-library error is covered by
        // SpcNativeSessionTests.Open_NativeLibraryMissing_ThrowsActionableError.
        using var f = SpcTempFile.Create(SpcFixture.Build());
        using IPlaybackCaptureSession session = new SpcPlaybackBackend().Open(
            new FileInfo(f.Path), new PlaybackOptions(MaxDurationSeconds: 1), new NullSink());
        session.Run();
        Assert.True(session.IsComplete);
        Assert.True(session.SamplePosition > 0);
    }

    [Fact]
    public void SnesDspVoices_AreExactlyEightStablePcmVoices()
    {
        var device = VisualizationDeviceCatalog.SnesDsp();
        Assert.Equal(ChipType.SnesDsp, device.Type);
        Assert.Equal(24_576_000, device.ClockHz);
        Assert.True(device.Capabilities.HasFlag(DeviceCapabilities.NativeVoiceAudio));
        Assert.Equal(ScopeSupport.Channel, device.ScopeSupport);

        var voices = VisualizationDeviceCatalog.SnesDspVoices();
        Assert.Equal(8, voices.Count);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal($"VOICE {i + 1}", voices[i].DisplayName);
            Assert.Equal(VoicePresentationKind.Pcm, voices[i].Presentation);
            Assert.Equal(VoiceKind.PcmVoice, voices[i].Kind);
            Assert.True(voices[i].SupportsPitch);
        }
    }

    private static PlaybackProbeResult Probe(SpcTempFile f) =>
        new SpcPlaybackBackend().Probe(new FileInfo(f.Path),
            new PlaybackEnvironment([Path.GetDirectoryName(f.Path)!]));

    private sealed class NullSink : IPlaybackEventSink
    {
        public void OnDevice(in DeviceDescriptor d) { }
        public void OnChipWrite(in TimedChipWrite w) { }
        public void OnMidi(in TimedMidiMessage m) { }
        public void OnSampleAsset(in TimedSampleAssetEvent a) { }
        public void OnLoopBoundary(in TimedLoopBoundary l) { }
    }
}internal sealed class SpcTempFile : IDisposable
{
    public string Path { get; }
    private SpcTempFile(string p) => Path = p;
    public static SpcTempFile Create(byte[] data)
    {
        string p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spc-{Guid.NewGuid():N}.spc");
        File.WriteAllBytes(p, data);
        return new SpcTempFile(p);
    }
    public void Dispose() { try { File.Delete(Path); } catch { } }
}

internal static class SpcFixture
{
    public static byte[] Build(int? sizeOverride = null)
    {
        const int fileSize = 0x10200;
        const int ramOffset = 0x100;
        const int dspOffset = 0x10100;

        byte[] data = new byte[fileSize];
        Encoding.ASCII.GetBytes(SpcMetadata.Signature).CopyTo(data, 0);
        data[0x23] = 0x30; /* format */
        data[0x24] = 1;    /* version */
        data[0x25] = 0x00; /* pcl -> PC = 0x0200 */
        data[0x26] = 0x02; /* pch */
        data[0x2B] = 0xFF; /* sp */

        /* CPU at 0x0200: key on voice 0 via $F2/$F3, then hang. */
        int pc = ramOffset + 0x0200;
        data[pc + 0] = 0x8F; data[pc + 1] = 0x4C; data[pc + 2] = 0xF2;
        data[pc + 3] = 0x8F; data[pc + 4] = 0x01; data[pc + 5] = 0xF3;
        data[pc + 6] = 0x2F; data[pc + 7] = 0xFE;

        /* DIR entry for source 0 at 0x0300: start = loop = 0x0400. */
        int dir = ramOffset + 0x0300;
        data[dir + 0] = 0x00; data[dir + 1] = 0x04;
        data[dir + 2] = 0x00; data[dir + 3] = 0x04;

        /* BRR block at 0x0400: end+loop block containing a non-zero sample. */
        int brr = ramOffset + 0x0400;
        data[brr] = 0xA3;
        for (int i = 0; i < 8; i++)
            data[brr + 1 + i] = 0xF0;

        data[dspOffset + 0x00] = 0x7F; /* voice 0 voll */
        data[dspOffset + 0x01] = 0x7F; /* voice 0 volr */
        data[dspOffset + 0x02] = 0x00; /* pitchl */
        data[dspOffset + 0x03] = 0x10; /* pitchh = 0x1000 (1.0x) */
        data[dspOffset + 0x04] = 0x00; /* srcn */
        data[dspOffset + 0x05] = 0xFF; /* adsr0 */
        data[dspOffset + 0x06] = 0xE0; /* adsr1 */
        data[dspOffset + 0x0C] = 0x7F; /* mvoll */
        data[dspOffset + 0x1C] = 0x7F; /* mvolr */
        data[dspOffset + 0x2C] = 0x00; /* evoll: echo return silent */
        data[dspOffset + 0x3C] = 0x00; /* evolr: echo return silent */
        data[dspOffset + 0x4C] = 0x00; /* kon: CPU-driven key-on */
        data[dspOffset + 0x4D] = 0x00; /* eon: no voice feeds echo */
        data[dspOffset + 0x5D] = 0x03; /* dir */
        data[dspOffset + 0x6C] = 0x00; /* flg */

        return sizeOverride.HasValue && sizeOverride.Value < data.Length
            ? data.AsSpan(0, sizeOverride.Value).ToArray() : data;
    }

    public static SpcMetadata BuildMetadata(
        string songTitle = "", string gameTitle = "", string artist = "",
        string dumper = "", string comment = "",
        int? playLengthSeconds = null, int? fadeLengthMilliseconds = null,
        byte[] songTitleBytes = null)
    {
        byte[] data = Build();
        byte[] song = songTitleBytes ?? (songTitle.Length > 0 ? Encoding.ASCII.GetBytes(songTitle) : null);
        if (song != null) Copy(data, 0x2E, 32, song);
        if (gameTitle.Length > 0) Copy(data, 0x4E, 32, Encoding.ASCII.GetBytes(gameTitle));
        if (artist.Length > 0) Copy(data, 0xB1, 32, Encoding.ASCII.GetBytes(artist));
        if (dumper.Length > 0) Copy(data, 0x6E, 16, Encoding.ASCII.GetBytes(dumper));
        if (comment.Length > 0) Copy(data, 0x7E, 32, Encoding.ASCII.GetBytes(comment));
        if (playLengthSeconds.HasValue) Digits(data, 0xA9, 3, playLengthSeconds.Value);
        if (fadeLengthMilliseconds.HasValue) Digits(data, 0xAC, 5, fadeLengthMilliseconds.Value);
        return SpcMetadataParser.Parse(data, new List<string>());
    }

    private static void Copy(byte[] d, int o, int l, byte[] v) => Array.Copy(v, 0, d, o, Math.Min(v.Length, l));
    private static void Digits(byte[] d, int o, int l, int v)
    {
        string t = v.ToString($"D{l}", System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < l; i++) d[o + i] = (byte)t[i];
    }
}
