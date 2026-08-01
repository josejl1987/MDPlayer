using Fmp.Core.Metadata;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public class MetadataTests
{
    static MetadataTests()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }
    [Fact]
    public void FromFmpFile_WithJapaneseTitle_ExtractsCorrectly()
    {
        string path = CreateTestOvi(
            comment: "タイトル曲\n作曲：テスト太郎\n(c) 2024",
            expectedTitle: "タイトル曲");

        var meta = FmpMetadata.FromFmpFile(path);
        Assert.Equal("タイトル曲", meta.Title);
        Assert.Contains("作曲：テスト太郎", meta.Comment);
        Assert.Equal("ovi", meta.SourceFormat, ignoreCase: true);
    }


    [Fact]
    public void FromFmpFile_WithUtf8JapaneseTitle_ExtractsCorrectly()
    {
        string path = CreateTestOvi(
            comment: "勝利の歌\nTRIUMPHAL SONG",
            expectedTitle: "勝利の歌",
            encoding: new System.Text.UTF8Encoding(false));

        var meta = FmpMetadata.FromFmpFile(path);
        Assert.Equal("勝利の歌", meta.Title);
    }

    [Fact]
    public void DecodeMetadata_TrailingDosEof_IsRemoved()
    {
        byte[] bytes = [.. System.Text.Encoding.GetEncoding(932).GetBytes("勝利の歌"), 0x1A, 0x00];
        Assert.Equal("勝利の歌", FmpMetadata.DecodeMetadata(bytes));
    }

    [Fact]
    public void FromFmpFile_WithEmptyFirstLine_UsesFirstNonEmpty()
    {
        string path = CreateTestOvi(
            comment: "\nSecond Line\nThird",
            expectedTitle: "Second Line");

        var meta = FmpMetadata.FromFmpFile(path);
        Assert.Equal("Second Line", meta.Title);
    }

    [Fact]
    public void FromFmpFile_WithControlSequences_StripsForTitle()
    {
        string path = CreateTestOvi(
            comment: "Nor\x01mal\x02 Song\nComposer",
            expectedTitle: "Normal Song");

        var meta = FmpMetadata.FromFmpFile(path);
        Assert.Equal("Normal Song", meta.Title);
        // Comment should keep the full raw text including control chars
        Assert.Contains("Nor\x01mal\x02 Song", meta.Comment);
    }

    [Fact]
    public void FromFmpFile_InvalidFile_FallsBackToDefaults()
    {
        string path = Path.GetTempFileName() + ".ovi";
        File.WriteAllBytes(path, new byte[] { 0, 0 }); // Invalid — too small

        var meta = FmpMetadata.FromFmpFile(path);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), meta.Title);
        Assert.Equal("", meta.Comment);
        Assert.NotEmpty(meta.SourceSha256);

        File.Delete(path);
    }

    [Fact]
    public void ToSafeFilename_ReplacesInvalidChars()
    {
        var meta = new FmpMetadata { Title = "Song: \"Test\" / Question?" };
        string safe = meta.ToSafeFilename('_');
        // On Linux only '/' is invalid; on Windows ':', '"', '/', '?' are all invalid
        Assert.DoesNotContain(Path.DirectorySeparatorChar == '/' ? '/' : '\\', safe);
        Assert.DoesNotContain('\0', safe);
    }

    [Fact]
    public void JsonOutput_IsValid()
    {
        var meta = new FmpMetadata
        {
            Title = "Test Song",
            Comment = "Test\nComposer",
            SourceFile = "test.ovi",
            RenderedSamples = 44100 * 10,
            SampleRate = 44100
        };

        string json = meta.ToJson();
        Assert.Contains("Test Song", json);
        Assert.Contains("44100", json);

        // Parsing back should not throw
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<FmpMetadata>(json);
        Assert.Equal("Test Song", deserialized.Title);
    }

    /// <summary>
    /// Create a synthetic OVI file with a given comment for testing.
    /// The OVI format: LE16 offset to FMC marker, then header data,
    /// then "FMC"+version, then null-terminated CP932 comment.
    /// </summary>
    private static string CreateTestOvi(
        string comment,
        string expectedTitle,
        System.Text.Encoding encoding = null)
    {
        string path = Path.GetTempFileName() + ".ovi";
        encoding ??= System.Text.Encoding.GetEncoding(932);
        byte[] commentBytes = encoding.GetBytes(comment);

        // Header: some fake frequency/duration data
        var header = new byte[0x200];
        // First 2 bytes = offset to FMC marker
        int fmcOffset = 0x200;
        header[0] = (byte)(fmcOffset & 0xFF);
        header[1] = (byte)((fmcOffset >> 8) & 0xFF);

        // FMC marker + version
        var fmc = new byte[] { (byte)'F', (byte)'M', (byte)'C', (byte)'I' };

        // Combine all parts
        using var ms = new MemoryStream();
        ms.Write(header, 0, header.Length);
        ms.Write(fmc, 0, fmc.Length);
        ms.Write(commentBytes, 0, commentBytes.Length);
        ms.WriteByte(0); // null terminator

        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }
}
