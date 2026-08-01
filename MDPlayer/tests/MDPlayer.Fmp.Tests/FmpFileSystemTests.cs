using Fmp.Core.IO;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public class FmpFileSystemTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _searchDir;

    public FmpFileSystemTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "fmpfs_test_" + Guid.NewGuid().ToString("N"));
        _searchDir = Path.Combine(_testDir, "banks");
        Directory.CreateDirectory(_searchDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    [Fact]
    public void CaseInsensitiveLookup()
    {
        // Create VOICE.PVI (uppercase)
        File.WriteAllText(Path.Combine(_searchDir, "VOICE.PVI"), "pcm data");

        var fs = new FmpFileSystem(new[] { _searchDir });

        // Request voice.pvi (lowercase)
        Assert.True(fs.TryReadFile(new DosPath("voice.pvi"), out var data, out var source));
        Assert.Equal("voice.pvi", source.RequestedName);
        Assert.EndsWith("VOICE.PVI", source.ResolvedPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MixedSeparatorLookup()
    {
        File.WriteAllText(Path.Combine(_searchDir, "sound.pvi"), "data");

        var fs = new FmpFileSystem(new[] { _searchDir });

        // Request with forward slash (DOS should accept both)
        Assert.True(fs.TryReadFile(new DosPath("SOUND.PVI"), out _, out var source));
        Assert.EndsWith("sound.pvi", source.ResolvedPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchPathPrecedence()
    {
        var dirA = Path.Combine(_testDir, "A");
        var dirB = Path.Combine(_testDir, "B");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        File.WriteAllText(Path.Combine(dirA, "track.pvi"), "from A");
        File.WriteAllText(Path.Combine(dirB, "track.pvi"), "from B");

        // A should win (first in search order)
        var fs = new FmpFileSystem(new[] { dirA, dirB });
        Assert.True(fs.TryReadFile(new DosPath("TRACK.PVI"), out var data, out _));
        Assert.Equal("from A"u8, data.Span.ToArray());

        // Reversed order: B should win
        var fs2 = new FmpFileSystem(new[] { dirB, dirA });
        Assert.True(fs2.TryReadFile(new DosPath("TRACK.PVI"), out var data2, out _));
        Assert.Equal("from B"u8, data2.Span.ToArray());
    }

    [Fact]
    public void AmbiguousMatch()
    {
        // Two files with same case-insensitive name
        File.WriteAllText(Path.Combine(_searchDir, "VOICE.PVI"), "one");
        File.WriteAllText(Path.Combine(_searchDir, "voice.pvi"), "two");

        var fs = new FmpFileSystem(new[] { _searchDir });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            fs.TryReadFile(new DosPath("Voice.Pvi"), out _, out _));
        Assert.Contains("Ambiguous", ex.Message);
    }

    [Fact]
    public void MissingFile()
    {
        var fs = new FmpFileSystem(new[] { _searchDir });
        Assert.False(fs.TryReadFile(new DosPath("NONEXIST.PVI"), out _, out _));
    }

    [Fact]
    public void PathTraversalRejection()
    {
        var fs = new FmpFileSystem(new[] { _searchDir });

        // Attempt to traverse outside search path
        var ex = Assert.Throws<InvalidOperationException>(() =>
            fs.TryReadFile(new DosPath("..\\..\\etc\\passwd"), out _, out _));
        Assert.Contains("traversal", ex.Message);
    }

    [Fact]
    public void JapaneseCp932Filename()
    {
        // Create a file with a CP932 filename
        string filename = "テスト.pvi";  // "test.pvi" in Japanese
        string filePath = Path.Combine(_searchDir, filename);
        File.WriteAllText(filePath, "japanese content");

        var fs = new FmpFileSystem(new[] { _searchDir });
        Assert.True(fs.TryReadFile(new DosPath(filename), out var data, out _));
        Assert.Equal("japanese content"u8, data.Span.ToArray());
    }

    [Fact]
    public void EmptySearchDirectories()
    {
        // Empty string in search paths should be ignored
        var fs = new FmpFileSystem(new[] { "", "  ", _searchDir });
        File.WriteAllText(Path.Combine(_searchDir, "test.pvi"), "ok");

        Assert.True(fs.TryReadFile(new DosPath("TEST.PVI"), out var data, out _));
        Assert.Equal("ok"u8, data.Span.ToArray());
    }

    [Fact]
    public void DuplicateSearchDirectories()
    {
        // Duplicate directories should not cause duplicate matches
        File.WriteAllText(Path.Combine(_searchDir, "VOICE.PVI"), "data");

        var fs = new FmpFileSystem(new[] { _searchDir, _searchDir });
        Assert.True(fs.TryReadFile(new DosPath("voice.pvi"), out _, out _));
    }

    [Fact]
    public void FileWithDirectoryPrefix()
    {
        // Request a file in a subdirectory: "BANKS\\VOICE.PVI"
        var subDir = Path.Combine(_searchDir, "BANKS");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "VOICE.PVI"), "subdir data");

        var fs = new FmpFileSystem(new[] { _searchDir });
        Assert.True(fs.TryReadFile(new DosPath("BANKS\\VOICE.PVI"), out var data, out _));
        Assert.Equal("subdir data"u8, data.Span.ToArray());
    }
}
