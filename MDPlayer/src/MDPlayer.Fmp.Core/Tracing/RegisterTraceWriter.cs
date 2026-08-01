using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fmp.Core.Tracing;

/// <summary>
/// Streaming JSONL trace writer for FMP register events (OPNA + PPZ8).
/// Fail-closed: the first write exception is captured; the renderer can
/// check <see cref="Failed"/> / <see cref="FirstError"/> each iteration
/// and abort the render with a nonzero exit.
/// Every event carries a sample position for timing alignment.
/// </summary>
internal class RegisterTraceWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private bool _failed;
    private Exception _firstError;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>True if a write error has occurred (fail-closed).</summary>
    public bool Failed => _failed;

    /// <summary>The first exception that caused the failed state, if any.</summary>
    public Exception FirstError => _firstError;

    /// <summary>Number of trace events written so far.</summary>
    public long TraceCount { get; private set; }

    /// <summary>Path to the trace output file.</summary>
    public string TracePath { get; }

    /// <summary>SHA-256 of the trace file (available after Dispose).</summary>
    public string TraceSha256 { get; private set; } = "";

    public RegisterTraceWriter(string path)
    {
        TracePath = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        _writer = new StreamWriter(path, false, new UTF8Encoding(false));
    }

    /// <summary>
    /// Write an OPNA (YM2608) register write event.
    /// </summary>
    public void WriteOpna(int port, int address, int data, long sample)
    {
        if (_failed || _disposed) return;

        try
        {
            var entry = new
            {
                ev = "opna",
                port,
                address,
                data,
                sample
            };
            var line = JsonSerializer.Serialize(entry, JsonOpts);
            _writer.WriteLine(line);
            TraceCount++;
        }
        catch (Exception ex)
        {
            CaptureError(ex);
        }
    }

    /// <summary>
    /// Write a PPZ8 PCM bank load event.
    /// </summary>
    public void WritePpz8Load(int bank, int mode, int entryCount, long totalBytes, string sha256, long sample)
    {
        if (_failed || _disposed) return;

        try
        {
            var entry = new
            {
                ev = "ppz8-load",
                bank,
                mode,
                entryCount,
                totalBytes,
                sha256,
                sample
            };
            var line = JsonSerializer.Serialize(entry, JsonOpts);
            _writer.WriteLine(line);
            TraceCount++;
        }
        catch (Exception ex)
        {
            CaptureError(ex);
        }
    }

    /// <summary>
    /// Write a PPZ8 register write event.
    /// </summary>
    public void WritePpz8Write(int port, int address, int data, long sample)
    {
        if (_failed || _disposed) return;

        try
        {
            var entry = new
            {
                ev = "ppz8-write",
                port,
                address,
                data,
                sample
            };
            var line = JsonSerializer.Serialize(entry, JsonOpts);
            _writer.WriteLine(line);
            TraceCount++;
        }
        catch (Exception ex)
        {
            CaptureError(ex);
        }
    }

    /// <summary>
    /// Flush the trace writer. Safe to call even after failure.
    /// </summary>
    public void Flush()
    {
        if (_disposed) return;
        try { _writer.Flush(); } catch { }
    }

    /// <summary>
    /// Close the trace writer and compute the trace file SHA-256.
    /// Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _writer.Flush();
            _writer.Close();
            _writer.Dispose();

            // Compute SHA-256 of the completed trace file
            if (File.Exists(TracePath))
            {
                using var fs = File.OpenRead(TracePath);
                TraceSha256 = Convert.ToHexString(SHA256.HashData(fs));
            }
        }
        catch
        {
            // Best-effort hash computation after close
        }
    }

    private void CaptureError(Exception ex)
    {
        if (!_failed)
        {
            _failed = true;
            _firstError = ex;
        }
    }
}
