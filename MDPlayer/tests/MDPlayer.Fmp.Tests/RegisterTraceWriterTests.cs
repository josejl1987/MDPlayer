using System.Security.Cryptography;
using System.Text;
using Fmp.Core.Tracing;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public class RegisterTraceWriterTests
{
    /// <summary>
    /// Writing OPNA events should produce valid JSONL with sample positions.
    /// </summary>
    [Fact]
    public void WriteOpna_ProducesJsonl()
    {
        string path = Path.GetTempFileName() + ".jsonl";
        try
        {
            using (var tw = new RegisterTraceWriter(path))
            {
                tw.WriteOpna(0, 0x28, 0xF0, 42);
                tw.WriteOpna(1, 0x28, 0xF1, 100);
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);

            var first = System.Text.Json.JsonDocument.Parse(lines[0]);
            Assert.Equal("opna", first.RootElement.GetProperty("ev").GetString());
            Assert.Equal(0, first.RootElement.GetProperty("port").GetInt32());
            Assert.Equal(0x28, first.RootElement.GetProperty("address").GetInt32());
            Assert.Equal(0xF0, first.RootElement.GetProperty("data").GetInt32());
            Assert.Equal(42, first.RootElement.GetProperty("sample").GetInt64());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Writing PPZ8 load events should produce valid JSONL with bank metadata.
    /// </summary>
    [Fact]
    public void WritePpz8Load_IncludesBankMetadata()
    {
        string path = Path.GetTempFileName() + ".jsonl";
        try
        {
            using (var tw = new RegisterTraceWriter(path))
            {
                tw.WritePpz8Load(0, 1, 5, 1024, "ABCD1234", 42);
            }

            var line = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(line);
            Assert.Equal("ppz8-load", doc.RootElement.GetProperty("ev").GetString());
            Assert.Equal(0, doc.RootElement.GetProperty("bank").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("mode").GetInt32());
            Assert.Equal(5, doc.RootElement.GetProperty("entryCount").GetInt32());
            Assert.Equal(1024, doc.RootElement.GetProperty("totalBytes").GetInt64());
            Assert.Equal("ABCD1234", doc.RootElement.GetProperty("sha256").GetString());
            Assert.Equal(42, doc.RootElement.GetProperty("sample").GetInt64());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// PPZ8 write events should include sample position.
    /// </summary>
    [Fact]
    public void WritePpz8Write_IncludesSample()
    {
        string path = Path.GetTempFileName() + ".jsonl";
        try
        {
            using (var tw = new RegisterTraceWriter(path))
            {
                tw.WritePpz8Write(0, 1, 0x80, 12345);
            }

            var line = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(line);
            Assert.Equal("ppz8-write", doc.RootElement.GetProperty("ev").GetString());
            Assert.Equal(12345, doc.RootElement.GetProperty("sample").GetInt64());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// TraceCount should increment with each event.
    /// </summary>
    [Fact]
    public void TraceCount_Increments()
    {
        string path = Path.GetTempFileName() + ".jsonl";
        try
        {
            var tw = new RegisterTraceWriter(path);
            Assert.Equal(0, tw.TraceCount);

            tw.WriteOpna(0, 0, 0, 0);
            Assert.Equal(1, tw.TraceCount);

            tw.WritePpz8Load(0, 0, 0, 0, "", 0);
            Assert.Equal(2, tw.TraceCount);

            tw.WritePpz8Write(0, 0, 0, 0);
            Assert.Equal(3, tw.TraceCount);

            tw.Dispose();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// After a write error, Failed should be true and further writes suppressed.
    /// Triggered by writing to a path that becomes invalid (e.g. directory removed).
    /// </summary>
    [Fact]
    public void FailClosed_AfterWriteError()
    {
        // Create a temp dir and open a trace writer inside it
        string dir = Path.Combine(Path.GetTempPath(), "trace_fail_test_" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "trace.jsonl");
        Directory.CreateDirectory(dir);

        var tw = new RegisterTraceWriter(path);
        Assert.False(tw.Failed);

        // First write succeeds
        tw.WriteOpna(0, 0, 0, 0);
        Assert.False(tw.Failed);
        Assert.Equal(1, tw.TraceCount);

        // Close the underlying StreamWriter to simulate an IO failure.
        // On Linux, deleting the directory doesn't invalidate the open fd,
        // so we force the error by closing the stream from the outside.
        var writerField = typeof(RegisterTraceWriter)
            .GetField("_writer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var stream = (System.IO.StreamWriter)writerField.GetValue(tw);
        stream.Close();

        // Write should now fail and set Failed flag
        tw.WriteOpna(0, 1, 2, 3);
        Assert.True(tw.Failed);
        Assert.NotNull(tw.FirstError);

        // Subsequent writes should be suppressed (no additional error)
        long countAfterFail = tw.TraceCount;
        tw.WritePpz8Load(0, 0, 0, 0, "", 0);
        Assert.Equal(countAfterFail, tw.TraceCount); // Count unchanged

        tw.Dispose();

        // Cleanup
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>
    /// Dispose should compute a non-empty SHA-256 of the trace file.
    /// </summary>
    [Fact]
    public void Dispose_ComputesTraceSha256()
    {
        string path = Path.GetTempFileName() + ".jsonl";
        try
        {
            var tw = new RegisterTraceWriter(path);
            tw.WriteOpna(0, 0x28, 0xF0, 42);
            tw.WritePpz8Load(1, 0, 3, 512, "TESTHASH", 99);
            tw.Dispose();

            Assert.False(string.IsNullOrEmpty(tw.TraceSha256));

            // Verify the hash matches what we'd compute externally
            using var fs = File.OpenRead(path);
            string expected = Convert.ToHexString(SHA256.HashData(fs));
            Assert.Equal(expected, tw.TraceSha256);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Multiple Dispose calls should be safe.
    /// </summary>
    [Fact]
    public void Dispose_MultipleCalls_DoesNotThrow()
    {
        string path = Path.GetTempFileName() + ".jsonl";
        try
        {
            var tw = new RegisterTraceWriter(path);
            tw.WriteOpna(0, 0, 0, 0);
            tw.Dispose();
            tw.Dispose(); // should not throw
            tw.Dispose(); // should not throw
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Trace file should contain valid JSONL (one JSON object per line).
    /// </summary>
    [Fact]
    public void TraceFile_IsValidJsonl()
    {
        string path = Path.GetTempFileName() + ".jsonl";
        try
        {
            using (var tw = new RegisterTraceWriter(path))
            {
                tw.WriteOpna(0, 0, 0, 0);
                tw.WritePpz8Write(1, 2, 3, 10);
                tw.WritePpz8Load(0, 0, 1, 256, "HASH", 20);
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(3, lines.Length);

            foreach (var line in lines)
            {
                // Each line must be parseable as JSON with an "ev" field
                var doc = System.Text.Json.JsonDocument.Parse(line);
                Assert.NotNull(doc.RootElement.GetProperty("ev").GetString());
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
