using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public class ScopePcmChunkWriterTests
{
    [Fact]
    public void WritesChunkedPcmFiles()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-chunks-").FullName;
        try
        {
            int sampleRate = 44100;
            double chunkDuration = 0.5;
            int chunkSize = (int)(sampleRate * chunkDuration);

            using (var writer = new ScopePcmChunkWriter(dir, "test", sampleRate, chunkDuration))
            {
                var data = new short[(int)(chunkSize * 1.2)];
                for (int i = 0; i < data.Length; i++)
                    data[i] = (short)(i * 100);

                writer.Write(data, 0, data.Length);
            }

            string[] files = Directory.GetFiles(dir, "*.pcm");
            Assert.True(files.Length >= 2);

            Assert.True(File.Exists(Path.Combine(dir, "0000.pcm")));
            Assert.True(File.Exists(Path.Combine(dir, "0001.pcm")));

            var chunk0 = ScopePcmChunkWriter.ReadChunk(Path.Combine(dir, "0000.pcm"));
            Assert.Equal(chunkSize, chunk0.Length);
            Assert.Equal(0, chunk0[0]);
            Assert.Equal((short)(100), chunk0[1]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EmptyWriterProducesNoChunks()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-chunks-").FullName;
        try
        {
            using (var writer = new ScopePcmChunkWriter(dir, "test", 44100, 1.0))
            {
            }

            Assert.Empty(Directory.GetFiles(dir, "*.pcm"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FlushWritesPartialChunk()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-chunks-").FullName;
        try
        {
            int chunkSize = 100;
            using (var writer = new ScopePcmChunkWriter(dir, "test", 100, 1.0))
            {
                var data = new short[chunkSize / 2];
                for (int i = 0; i < data.Length; i++)
                    data[i] = 42;
                writer.Write(data, 0, data.Length);
                writer.Flush();
            }

            var files = Directory.GetFiles(dir, "*.pcm");
            Assert.Single(files);

            var chunk = ScopePcmChunkWriter.ReadChunk(files[0]);
            Assert.Equal(chunkSize / 2, chunk.Length);
            Assert.All(chunk, v => Assert.Equal(42, v));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GetChunkPath_ReturnsNullForMissingChunk()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-chunks-").FullName;
        try
        {
            var path = ScopePcmChunkWriter.GetChunkPath(dir, 5000, 44100);
            Assert.Null(path);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WriteMultipleChunksAndVerifyIntegrity()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-chunks-").FullName;
        try
        {
            int sampleRate = 100;
            int totalSamples = 250;

            using (var writer = new ScopePcmChunkWriter(dir, "test", sampleRate, 1.0))
            {
                var data = new short[totalSamples];
                for (int i = 0; i < totalSamples; i++)
                    data[i] = (short)(i - 32768);

                writer.Write(data, 0, totalSamples);
            }

            Assert.True(File.Exists(Path.Combine(dir, "0000.pcm")));
            Assert.True(File.Exists(Path.Combine(dir, "0001.pcm")));
            Assert.True(File.Exists(Path.Combine(dir, "0002.pcm")));

            var chunk0 = ScopePcmChunkWriter.ReadChunk(Path.Combine(dir, "0000.pcm"));
            Assert.Equal(100, chunk0.Length);
            Assert.Equal(-32768, chunk0[0]);
            Assert.Equal(-32768 + 99, chunk0[99]);

            var chunk2 = ScopePcmChunkWriter.ReadChunk(Path.Combine(dir, "0002.pcm"));
            Assert.Equal(50, chunk2.Length);
            Assert.Equal(200 - 32768, chunk2[0]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
