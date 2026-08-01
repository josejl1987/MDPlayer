using Fmp.Core.Audio;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public class WavWriterTests
{
    [Fact]
    public void WriteAndClose_ProducesValidWav()
    {
        string path = Path.GetTempFileName() + ".wav";
        try
        {
            // Write 1 second of 440Hz sine wave
            int sampleRate = 44100;
            int channels = 2;
            int totalSamples = sampleRate;
            var samples = new short[totalSamples * 2];
            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sampleRate;
                short val = (short)(Math.Sin(t * 440 * Math.PI * 2) * 8000);
                samples[i * 2] = val;
                samples[i * 2 + 1] = val;
            }

            var wav = new WavWriter(path, sampleRate, channels);
            wav.Write(samples);
            wav.Close();

            // Verify file exists and has correct size
            var fi = new FileInfo(path);
            Assert.True(fi.Exists);

            // Read header
            using var fs = File.OpenRead(path);
            byte[] header = new byte[44];
            fs.Read(header, 0, 44);

            // RIFF
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
            // WAVE
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(header, 8, 4));
            // fmt  (format tag)
            Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(header, 12, 4));
            // PCM format = 1
            Assert.Equal(1, header[20] + (header[21] << 8));
            // Stereo = 2
            Assert.Equal(2, header[22] + (header[23] << 8));
            // Sample rate = 44100
            Assert.Equal(44100, header[24] + (header[25] << 8) + (header[26] << 16) + (header[27] << 24));
            // Bits per sample = 16
            Assert.Equal(16, header[34] + (header[35] << 8));

            // data chunk
            Assert.Equal("data", System.Text.Encoding.ASCII.GetString(header, 36, 4));

            // Expected data size: totalSamples * channels * bytesPerSample
            int expectedDataSize = totalSamples * channels * 2;
            int actualDataSize = header[40] + (header[41] << 8) + (header[42] << 16) + (header[43] << 24);
            Assert.Equal(expectedDataSize, actualDataSize);

            // Expected file size: 44 + dataSize
            int expectedFileSize = 44 + expectedDataSize;
            Assert.Equal(expectedFileSize, fi.Length);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void DisposeWithoutClose_DeletesTempFile()
    {
        string path = Path.GetTempFileName() + ".wav";
        string tempPath = path + ".tmp";

        var wav = new WavWriter(path);
        wav.Write(new short[] { 1, 2, 3, 4 });
        wav.Dispose(); // Dispose without Close

        // Temp file should be deleted
        Assert.False(File.Exists(tempPath));
        // Final file should NOT exist
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void InterruptedRender_DoesNotLeaveValidFinalFile()
    {
        string path = Path.GetTempFileName() + ".wav";

        var wav = new WavWriter(path);
        wav.Write(new short[100]);
        // Dispose without Close — temp file deleted, final file never created
        wav.Dispose();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void EmptyWrite_StillProducesValidHeader()
    {
        string path = Path.GetTempFileName() + ".wav";
        try
        {
            var wav = new WavWriter(path);
            wav.Close(); // close without writing data

            var fi = new FileInfo(path);
            Assert.True(fi.Exists);
            Assert.Equal(44, fi.Length); // Header only

            // Verify it's a valid WAV header
            using var fs = File.OpenRead(path);
            byte[] header = new byte[44];
            fs.Read(header, 0, 44);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
            Assert.Equal(36, header[4] + (header[5] << 8) + (header[6] << 16) + (header[7] << 24)); // file size - 8
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
