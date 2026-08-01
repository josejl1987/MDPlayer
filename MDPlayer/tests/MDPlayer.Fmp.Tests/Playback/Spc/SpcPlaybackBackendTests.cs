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
        byte[] data = new byte[SpcMetadata.MinimumFileSize];
        Encoding.ASCII.GetBytes(SpcMetadata.Signature).CopyTo(data, 0);
        data[0x23] = 31; data[0x2C] = 0xFF; data[0x2D] = 0xEF;
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
