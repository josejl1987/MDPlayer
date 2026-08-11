using Fmp.Benchmarks;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiFixtureResolverTests
{
    [Fact]
    public void MissingFixtureIsRejected()
    {
        Assert.Null(MidiFixtureResolver.Resolve("does-not-exist.vgz"));
    }

    [Fact]
    public void UnsupportedFixtureIsRejected()
    {
        Assert.Null(MidiFixtureResolver.Resolve("does-not-exist.mid"));
    }

    [Fact]
    public void RepositoryFixtureSelectionIsStable()
    {
        string? first = MidiFixtureResolver.Resolve(null);
        string? second = MidiFixtureResolver.Resolve(null);
        Assert.Equal(first, second);
        Assert.NotNull(first);
        Assert.True(Path.GetExtension(first).Equals(".vgz", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(first).Equals(".ovi", StringComparison.OrdinalIgnoreCase));
    }
}
