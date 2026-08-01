using Fmp.Core.Audio;
using Fmp.Core.IO;
using Fmp.Core.Rendering;
using Fmp.Core.Tracing;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public class FmpRuntimeTests
{
    /// <summary>
    /// Test that FmpRuntime can be constructed and reports correct defaults.
    /// </summary>
    [Fact]
    public void ConstructWithDefaults()
    {
        var chipSink = new TestChipSink();
        var assets = new FmpRuntimeAssets("/nonexistent/FMP.COM"); // FMP.COM not available
        var rt = new FmpRuntime(chipSink, assets);

        Assert.True(rt.IsStopped); // Not initialized yet, so stopped
        Assert.Equal(0, rt.SamplePosition);
        Assert.Equal(0, rt.CurrentLoop);
        Assert.Equal(2, rt.LoopCount);
    }

    /// <summary>
    /// Test that initialization with missing FMP.COM throws or reports error.
    /// </summary>
    [Fact]
    public void InitWithMissingFmpCom_FailsGracefully()
    {
        var chipSink = new TestChipSink();
        var assets = new FmpRuntimeAssets("/nonexistent/FMP.COM");
        var rt = new FmpRuntime(chipSink, assets);

        var ex = Record.Exception(() =>
            rt.Initialize(new byte[] { 0, 1, 2, 3 }, "test.ovi"));

        // Should throw either FileNotFoundException or IOException on Linux
        Assert.NotNull(ex);
        Assert.Contains("FMP.COM", ex.Message);
    }

    /// <summary>
    /// Test that initialization succeeds with FMP.COM available.
    /// This requires the test fixture FMP.COM in the output directory.
    /// </summary>
    [SkippableFact]
    public void InitWithFmpCom_Succeeds()
    {
        string fmpComPath = Path.GetFullPath("testfixtures/FMP.COM");
        Skip.IfNot(File.Exists(fmpComPath),
            $"FMP.COM not at expected path: {fmpComPath}.");

        var chipSink = new TestChipSink();
        var assets = new FmpRuntimeAssets(fmpComPath);

        // Use a search path that includes test fixtures for PVI banks
        var testDir = Path.GetDirectoryName(fmpComPath);
        var fileSystem = new FmpFileSystem(new[] { testDir });

        var rt = new FmpRuntime(chipSink, assets, fileSystem);

        // Load an OVI fixture
        string oviPath = Path.Combine(testDir, "test.ovi");
        byte[] oviData = new byte[100];

        rt.Initialize(oviData, oviPath);

        // After init, the runtime should be running (not stopped)
        Assert.False(rt.IsStopped);
    }

    /// <summary>
    /// TraceWriter should be null by default.
    /// </summary>
    [Fact]
    public void TraceWriter_DefaultIsNull()
    {
        var chipSink = new TestChipSink();
        var assets = new FmpRuntimeAssets("/nonexistent/FMP.COM");
        var rt = new FmpRuntime(chipSink, assets);

        Assert.Null(rt.TraceWriter);
    }

    /// <summary>
    /// Setting TraceWriter should not throw and should be returned by getter.
    /// </summary>
    [Fact]
    public void TraceWriter_SetAndGet()
    {
        var chipSink = new TestChipSink();
        var assets = new FmpRuntimeAssets("/nonexistent/FMP.COM");
        var rt = new FmpRuntime(chipSink, assets);

        string tracePath = Path.GetTempFileName() + ".jsonl";
        try
        {
            using var tw = new RegisterTraceWriter(tracePath);
            rt.TraceWriter = tw;
            Assert.Same(tw, rt.TraceWriter);

            // Set to null (unhook)
            rt.TraceWriter = null;
            Assert.Null(rt.TraceWriter);
        }
        finally
        {
            if (File.Exists(tracePath)) File.Delete(tracePath);
        }
    }

    /// <summary>
    /// Callbacks should not throw when TraceWriter is set.
    /// </summary>
    [Fact]
    public void TraceWriter_AttachedBeforeInit_DoesNotThrow()
    {
        var chipSink = new TestChipSink();
        var assets = new FmpRuntimeAssets("/nonexistent/FMP.COM");
        var rt = new FmpRuntime(chipSink, assets);

        string tracePath = Path.GetTempFileName() + ".jsonl";
        try
        {
            using var tw = new RegisterTraceWriter(tracePath);
            rt.TraceWriter = tw;

            // Initialize will fail because FMP.COM is missing,
            // but the trace writer attachment itself should not cause issues.
            var ex = Record.Exception(() =>
                rt.Initialize(new byte[] { 0, 1, 2, 3 }, "test.ovi"));
            Assert.NotNull(ex); // Expected: FMP.COM not found
        }
        finally
        {
            if (File.Exists(tracePath)) File.Delete(tracePath);
        }
    }
}

/// <summary>
/// Test implementation of IFmpChipSink that records all events.
/// </summary>
public class TestChipSink : IFmpChipSink
{
    public List<(int chipId, int port, int address, int value, long sample)> Ym2608Writes = new();
    public List<(int bank, int mode, int count, long sample)> Ppz8Loads = new();
    public List<(int port, int address, int value)> Ppz8Writes = new();

    public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition)
    {
        Ym2608Writes.Add((chipId, port, address, value, samplePosition));
    }

    public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition)
    {
        Ppz8Loads.Add((bank, mode, samples.Length, samplePosition));
    }

    public void WritePpz8(int port, int address, int value, long samplePosition)
    {
        Ppz8Writes.Add((port, address, value));
    }
}
