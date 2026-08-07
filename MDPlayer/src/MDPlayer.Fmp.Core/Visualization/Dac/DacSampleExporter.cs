using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fmp.Core.Visualization;

/// <summary>
/// Exports the deduplicated DAC sample assets as one WAV per unique asset plus
/// a machine-readable manifest (spec §29). One file per asset, never one per
/// trigger. The WAV header sample rate is a documented neutral default and does
/// not participate in asset identity; playback rate is a playback-instance
/// property recorded separately.
/// </summary>
internal sealed record DacSampleExporterResult(
    IReadOnlyList<DacSampleExportFile> Assets,
    byte[] ManifestUtf8);

internal sealed record DacSampleExportFile(int AssetId, string FileName, byte[] WavBytes);

internal sealed class DacSampleExporter
{
    /// <summary>Neutral/default WAV sample rate for exported DAC assets (spec §29 policy 1).</summary>
    public const int DefaultWavSampleRate = 8000;

    public DacSampleExporter(int wavSampleRate = DefaultWavSampleRate)
    {
        if (wavSampleRate <= 0 || wavSampleRate > 0xFFFF_FFFF)
            throw new ArgumentOutOfRangeException(nameof(wavSampleRate));
        _wavSampleRate = wavSampleRate;
    }

    private readonly int _wavSampleRate;

    public DacSampleExporterResult Export(DacAnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var files = new List<DacSampleExportFile>();
        var manifestSamples = new List<object>();
        foreach (DacSampleAsset asset in report.Assets)
        {
            string fileName = $"dac_s{asset.AssetId:000}.wav";
            files.Add(new DacSampleExportFile(asset.AssetId, fileName, WriteWav8BitMono(asset.Payload.Span)));
            manifestSamples.Add(new
            {
                assetId = asset.AssetId,
                name = asset.StableName,
                file = fileName,
                hash = asset.ContentHash.Hex,
                format = new
                {
                    encoding = "pcm",
                    bitsPerSample = asset.Format.BitsPerSample,
                    signedness = asset.Format.Signedness.ToString().ToLowerInvariant(),
                    channels = asset.Format.ChannelCount,
                    sampleRate = _wavSampleRate,
                },
                displayBank = asset.DisplayBank,
                displayNote = asset.DisplayNote,
                triggerCount = asset.TriggerCount,
            });
        }

        var manifest = new { samples = manifestSamples };
        byte[] manifestUtf8 = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            new JsonSerializerOptions { WriteIndented = true });
        return new DacSampleExporterResult(files, manifestUtf8);
    }

    /// <summary>Writes an 8-bit mono uncompressed PCM WAV header + payload.</summary>
    private byte[] WriteWav8BitMono(ReadOnlySpan<byte> payload)
    {
        int dataBytes = payload.Length;
        // RIFF header is 44 bytes for an 8-bit mono PCM stream.
        var result = new byte[44 + dataBytes];
        WriteAscii(result, 0, "RIFF");
        WriteU32(result, 4, (uint)(36 + dataBytes));
        WriteAscii(result, 8, "WAVE");
        WriteAscii(result, 12, "fmt ");
        WriteU32(result, 16, 16);
        WriteU16(result, 20, 1);            // PCM
        WriteU16(result, 22, 1);            // mono
        WriteU32(result, 24, (uint)_wavSampleRate);
        WriteU32(result, 28, (uint)_wavSampleRate); // byte rate (1 byte * freq)
        WriteU16(result, 32, 1);            // block align
        WriteU16(result, 34, 8);            // bits per sample
        WriteAscii(result, 36, "data");
        WriteU32(result, 40, (uint)dataBytes);
        payload.CopyTo(result.AsSpan(44));
        return result;
    }

    private static void WriteAscii(byte[] buffer, int offset, string value)
    {
        foreach (char c in value)
            buffer[offset++] = (byte)c;
    }

    private static void WriteU16(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteU32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}