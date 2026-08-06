using Fmp.Core.PlaybackAssets;
using Fmp.Core.PlaybackAssets.Furnace;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.PlaybackAssets;

/// <summary>
/// End-to-end deduplication tests through the full collector pipeline, covering
/// the algorithm-aware carrier-TL normalization mandated by the timbre-identity
/// design. These replace the old literal-42-byte-TFI dedup rule.
/// </summary>
public class PlaybackAssetDedupTests
{
    private static ChipWriteEvent Write(byte address, byte data) => new()
    {
        ChipType = ChipType.Ym2608,
        ChipIndex = 0,
        Port = 0,
        Address = address,
        Data = data,
        WriteIndex = null,
        PlaybackSample = null,
    };

    /// <summary>Builds a full four-operator patch, sets algorithm/feedback and
    /// emits a key-on, all through the collector.</summary>
    private static void Emit(
        PlaybackAssetCollector collector,
        byte algorithm,
        byte feedback,
        int op1Tl, int op2Tl, int op3Tl, int op4Tl,
        int? op4Multiplier = null)
    {
        // Operator multiplier/detune registers (0x30 base; low nibble MT).
        collector.Observe(Write(0x30, 0x00)); // Op1
        collector.Observe(Write(0x34, 0x00)); // Op2
        collector.Observe(Write(0x38, 0x00)); // Op3
        collector.Observe(Write(0x3C, 0x00)); // Op4

        collector.Observe(Write(0x40, (byte)(op1Tl & 0x7F))); // Op1 TL
        collector.Observe(Write(0x44, (byte)(op2Tl & 0x7F))); // Op2 TL
        collector.Observe(Write(0x48, (byte)(op3Tl & 0x7F))); // Op3 TL
        collector.Observe(Write(0x4C, (byte)(op4Tl & 0x7F))); // Op4 TL

        if (op4Multiplier is { } mult)
            collector.Observe(Write(0x3C, (byte)mult)); // Op4 DT/MT

        // Feedback (0x38) | algorithm.
        collector.Observe(Write(0xB0, (byte)(((feedback & 0x07) << 3) | (algorithm & 0x07))));

        collector.Observe(Write(0x28, 0xF0)); // key-on channel 0, all operators
    }

    // --- 1. single-carrier volume changes merge ---
    [Fact]
    public void SingleCarrierVolumeChanges_MergeIntoOneInstrument()
    {
        var collector = new PlaybackAssetCollector();

        Emit(collector, algorithm: 0, feedback: 0,
            op1Tl: 10, op2Tl: 10, op3Tl: 10, op4Tl: 20); // Op4 carrier TL=20
        Emit(collector, algorithm: 0, feedback: 0,
            op1Tl: 10, op2Tl: 10, op3Tl: 10, op4Tl: 55); // Op4 carrier TL=55

        var snapshot = collector.Complete();
        Assert.Single(snapshot.Instruments);

        var instrument = snapshot.Instruments[0];
        // Canonical Op4 TL = 0 (loudest carrier), two level observations.
        Assert.Equal(0, instrument.Representative.Op4.TotalLevel);
        Assert.Equal(2, instrument.ObservationCount);
        Assert.Equal(2, instrument.CarrierLevelVariants.Count);
        Assert.Equal(20, instrument.MinimumObservedVolumeOffset);
        Assert.Equal(55, instrument.MaximumObservedVolumeOffset);
    }

    // --- 2. modulator changes do not merge ---
    [Fact]
    public void ModulatorTlChange_CreatesSecondInstrument()
    {
        var collector = new PlaybackAssetCollector();

        // Algorithm 0: Op4 is the only carrier; Op1-Op3 are modulators.
        Emit(collector, algorithm: 0, feedback: 0,
            op1Tl: 10, op2Tl: 10, op3Tl: 20, op4Tl: 30);
        Emit(collector, algorithm: 0, feedback: 0,
            op1Tl: 10, op2Tl: 10, op3Tl: 21, op4Tl: 30); // Op3 modulator TL changed

        var snapshot = collector.Complete();
        Assert.Equal(2, snapshot.Instruments.Count);
    }

    // --- 3. uniform multi-carrier shift merges ---
    [Fact]
    public void UniformMultiCarrierShift_MergesIntoOneInstrument()
    {
        var collector = new PlaybackAssetCollector();

        // Algorithm 4: carriers Op2, Op4.
        Emit(collector, algorithm: 4, feedback: 0,
            op1Tl: 0, op2Tl: 20, op3Tl: 0, op4Tl: 30);
        Emit(collector, algorithm: 4, feedback: 0,
            op1Tl: 0, op2Tl: 35, op3Tl: 0, op4Tl: 45);

        var snapshot = collector.Complete();
        Assert.Single(snapshot.Instruments);

        var instrument = snapshot.Instruments[0];
        // Both normalize to Op2=0, Op4=10.
        Assert.Equal(0, instrument.Representative.Op2.TotalLevel);
        Assert.Equal(10, instrument.Representative.Op4.TotalLevel);
        Assert.Equal(2, instrument.ObservationCount);
        Assert.Equal(20, instrument.MinimumObservedVolumeOffset);
        Assert.Equal(35, instrument.MaximumObservedVolumeOffset);
    }

    // --- 4. relative carrier balance remains distinct ---
    [Fact]
    public void DifferentCarrierBalance_CreatesSecondInstrument()
    {
        var collector = new PlaybackAssetCollector();

        // Algorithm 4: carriers Op2, Op4. Same Op2 TL, different Op4 TL.
        Emit(collector, algorithm: 4, feedback: 0,
            op1Tl: 0, op2Tl: 20, op3Tl: 0, op4Tl: 30); // normalizes to 0, 10
        Emit(collector, algorithm: 4, feedback: 0,
            op1Tl: 0, op2Tl: 20, op3Tl: 0, op4Tl: 40); // normalizes to 0, 20

        var snapshot = collector.Complete();
        Assert.Equal(2, snapshot.Instruments.Count);
    }

    // --- 5. non-TL parameters always remain significant ---
    [Fact]
    public void CarrierMultiplierChange_CreatesSecondInstrument()
    {
        var collector = new PlaybackAssetCollector();

        // Algorithm 0: Op4 is the single carrier. Same TL, different multiplier.
        Emit(collector, algorithm: 0, feedback: 0,
            op1Tl: 0, op2Tl: 0, op3Tl: 0, op4Tl: 30, op4Multiplier: 1);
        Emit(collector, algorithm: 0, feedback: 0,
            op1Tl: 0, op2Tl: 0, op3Tl: 0, op4Tl: 30, op4Multiplier: 2);

        var snapshot = collector.Complete();
        Assert.Equal(2, snapshot.Instruments.Count);
    }

    // --- 6. algorithm 7 feedback guard ---
    [Fact]
    public void Algorithm7_NonzeroFeedback_Op1TlChange_CreatesSecondInstrument()
    {
        var collector = new PlaybackAssetCollector();

        // Algorithm 7, feedback 5. Op1 owns the feedback loop, so its TL is
        // timbre-significant and must not merge even under a pure level change.
        Emit(collector, algorithm: 7, feedback: 5,
            op1Tl: 20, op2Tl: 10, op3Tl: 10, op4Tl: 10);
        Emit(collector, algorithm: 7, feedback: 5,
            op1Tl: 30, op2Tl: 10, op3Tl: 10, op4Tl: 10);

        var snapshot = collector.Complete();
        Assert.Equal(2, snapshot.Instruments.Count);
        Assert.All(snapshot.Instruments, i => Assert.True(i.FeedbackCarrierPreserved));
    }

    // --- algorithm 7 feedback: uniform shift of safe carriers still merges ---
    [Fact]
    public void Algorithm7_NonzeroFeedback_UniformSafeCarrierShift_Merges()
    {
        var collector = new PlaybackAssetCollector();

        // Op1 constant (so identity stays stable); Op2/3/4 shift uniformly.
        Emit(collector, algorithm: 7, feedback: 5,
            op1Tl: 20, op2Tl: 30, op3Tl: 35, op4Tl: 25);
        Emit(collector, algorithm: 7, feedback: 5,
            op1Tl: 20, op2Tl: 45, op3Tl: 50, op4Tl: 40);

        var snapshot = collector.Complete();
        Assert.Single(snapshot.Instruments);

        var instrument = snapshot.Instruments[0];
        Assert.True(instrument.FeedbackCarrierPreserved);
        // Op1 kept at representative observed TL (20).
        Assert.Equal(20, instrument.Representative.Op1.TotalLevel);
        // Safe carriers normalized to loudest normalizable (Op4=25 -> 0):
        // Op2=5, Op3=10, Op4=0.
        Assert.Equal(5, instrument.Representative.Op2.TotalLevel);
        Assert.Equal(10, instrument.Representative.Op3.TotalLevel);
        Assert.Equal(0, instrument.Representative.Op4.TotalLevel);
        Assert.Equal(2, instrument.ObservationCount);
    }

    // --- algorithm 0 exports a canonical instrument with Op4 at 0 ---
    [Fact]
    public void Export_CanonicalizesLoudestCarrierToZero()
    {
        var collector = new PlaybackAssetCollector();
        Emit(collector, algorithm: 0, feedback: 0,
            op1Tl: 12, op2Tl: 14, op3Tl: 16, op4Tl: 38);

        // Verify the exported TFI file is canonicalized via a real export.
        string dir = Path.Combine(Path.GetTempPath(), "tfi-canon-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = FurnaceAssetExporter.Export(collector.Complete(), dir);
            Assert.False(result.Failed);
            var file = Directory.GetFiles(Path.Combine(dir, "fm"), "*.tfi").Single();
            byte[] bytes = File.ReadAllBytes(file);
            // TFI operator order is Op1, Op3, Op2, Op4. Op4 TL is at offset 0x22.
            Assert.Equal(0, bytes[0x22]); // Op4 canonicalized to TL 0
            Assert.Equal(12, bytes[0x04]); // Op1 modulator TL untouched
            Assert.Equal(16, bytes[0x0E]); // Op3 modulator TL untouched
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // --- manifest records identity normalization ---
    [Fact]
    public void Manifest_RecordsIdentityNormalization()
    {
        var collector = new PlaybackAssetCollector();
        Emit(collector, algorithm: 4, feedback: 0,
            op1Tl: 0, op2Tl: 18, op3Tl: 0, op4Tl: 28);
        Emit(collector, algorithm: 4, feedback: 0,
            op1Tl: 0, op2Tl: 47, op3Tl: 0, op4Tl: 57); // uniform +29 shift

        string dir = Path.Combine(Path.GetTempPath(), "tfi-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = FurnaceAssetExporter.Export(collector.Complete(), dir);
            Assert.False(result.Failed);
            string json = File.ReadAllText(Path.Combine(dir, "manifest.json"));
            Assert.Contains("\"identityNormalization\": {", json);
            Assert.Contains("\"carrierTlNormalized\": true", json);
            Assert.Contains("\"feedbackCarrierPreserved\": false", json);
            Assert.Contains("\"observedVolumeOffsetMin\": 18", json);
            Assert.Contains("\"observedVolumeOffsetMax\": 47", json);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // --- manifest records feedback-preserved normalization ---
    [Fact]
    public void Manifest_RecordsFeedbackCarrierPreserved()
    {
        var collector = new PlaybackAssetCollector();
        Emit(collector, algorithm: 7, feedback: 5,
            op1Tl: 30, op2Tl: 10, op3Tl: 10, op4Tl: 10);

        string dir = Path.Combine(Path.GetTempPath(), "tfi-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = FurnaceAssetExporter.Export(collector.Complete(), dir);
            Assert.False(result.Failed);
            string json = File.ReadAllText(Path.Combine(dir, "manifest.json"));
            Assert.Contains("\"feedbackCarrierPreserved\": true", json);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
