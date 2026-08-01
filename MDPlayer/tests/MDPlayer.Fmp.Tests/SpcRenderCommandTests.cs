using System.Buffers.Binary;
using Fmp.Cli;
using MDPlayer.Fmp.Tests.Playback.Spc;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class SpcRenderCommandTests
{
    [Fact]
    public void Render_WritesNative32KhzMasterWav()
    {
        using var input = SpcTempFile.Create(SpcFixture.Build());
        string stem = Path.Combine(Path.GetTempPath(), $"mdplayer-spc-render-{Guid.NewGuid():N}");
        string output = stem + ".wav";
        string metadata = stem + ".metadata.json";

        try
        {
            int exitCode = RenderCommand.Handle(
            [
                input.Path,
                "--output", output,
                "--duration", "1",
                "--fade", "0",
                "--overwrite",
                "--quiet",
            ]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(output));
            Assert.True(File.Exists(metadata));

            using var stream = File.OpenRead(output);
            byte[] header = new byte[44];
            Assert.Equal(header.Length, stream.Read(header, 0, header.Length));
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(header, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(header, 8, 4));
            Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22, 2)));
            Assert.Equal(32_000, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24, 4)));
        }
        finally
        {
            TryDelete(output);
            TryDelete(metadata);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
