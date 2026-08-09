using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.PlaybackAssets;

/// <summary>
/// T1 — unit tests for <see cref="InstrumentIdentity"/> equality (by canonical
/// string), <see cref="MidiTrackKey"/> composition, and
/// <see cref="DeterministicIdentityTable"/> dedup stability + monotonic determinism.
/// </summary>
public sealed class InstrumentIdentityTests
{
    [Fact]
    public void Identity_Equality_IsByCanonicalOnly()
    {
        var a = new InstrumentIdentity(IdentityFamily.Fm, 7, "fm:007");
        var b = new InstrumentIdentity(IdentityFamily.Fm, 999, "fm:007"); // same canonical, different number
        var c = new InstrumentIdentity(IdentityFamily.Fm, 8, "fm:008");

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Identity_DifferentFamilies_SameCanonical_AreDistinct()
    {
        // Canonical strings differ across families, so equality stays correct.
        var fm = new InstrumentIdentity(IdentityFamily.Fm, 7, "fm:007");
        var pcm = new InstrumentIdentity(IdentityFamily.Pcm, 7, "pcm:007");
        Assert.NotEqual(fm, pcm);
    }

    [Fact]
    public void Identity_DisplayName_FmUsesZeroPaddedNumber_ElseCanonical()
    {
        Assert.Equal("FM 007", new InstrumentIdentity(IdentityFamily.Fm, 7, "fm:007").DisplayName);
        Assert.Equal("FM 042", new InstrumentIdentity(IdentityFamily.Fm, 42, "fm:042").DisplayName);
        Assert.Equal("ssg:tone", new InstrumentIdentity(IdentityFamily.Ssg, 0, "ssg:tone").DisplayName);
        Assert.Equal("rhythm:003", new InstrumentIdentity(IdentityFamily.Rhythm, 3, "rhythm:003").DisplayName);
    }

    [Fact]
    public void Identity_TryParse_ResolvesCanonicalForms_AndRejectsPlaceholders()
    {
        Assert.True(InstrumentIdentity.TryParse("fm:007", out var fm));
        Assert.Equal(IdentityFamily.Fm, fm.Family);
        Assert.Equal(7, fm.DedupNumber);

        Assert.True(InstrumentIdentity.TryParse("ssg:envelope:4", out var ssg));
        Assert.Equal(IdentityFamily.Ssg, ssg.Family);
        Assert.Equal("ssg:envelope:4", ssg.Canonical);

        Assert.True(InstrumentIdentity.TryParse("rhythm:002", out var rh));
        Assert.Equal(IdentityFamily.Rhythm, rh.Family);

        Assert.True(InstrumentIdentity.TryParse("dac:005", out var dac));
        Assert.Equal(IdentityFamily.Pcm, dac.Family);

        // Placeholder / unresolved tokens are not canonical identities.
        Assert.False(InstrumentIdentity.TryParse("ym2608:deadbeef", out _));
        Assert.False(InstrumentIdentity.TryParse("ym2612:fm:1", out _));
        Assert.False(InstrumentIdentity.TryParse("", out _));
    }

    [Fact]
    public void MidiTrackKey_ComposesChipChannelInstrument()
    {
        var inst = new InstrumentIdentity(IdentityFamily.Fm, 7, "fm:007");
        var keyA = new MidiTrackKey(ChipType.Ym2608, 1, inst);
        var keyB = new MidiTrackKey(ChipType.Ym2608, 1, inst);
        var keyOtherChannel = new MidiTrackKey(ChipType.Ym2608, 2, inst);
        var keyOtherChip = new MidiTrackKey(ChipType.Ym2612, 1, inst);

        Assert.Equal(keyA, keyB);
        Assert.NotEqual(keyA, keyOtherChannel);
        Assert.NotEqual(keyA, keyOtherChip);
    }
}

public sealed class DeterministicIdentityTableTests
{
    [Fact]
    public void GetOrAdd_SameHash_SameNumber_PrefixIndependentOfOrder()
    {
        var table = new DeterministicIdentityTable();
        string hash = DeterministicIdentityTable.HashText("patched-opn-normalized");

        var first = table.GetOrAdd(IdentityFamily.Fm, hash, "fm");
        var second = table.GetOrAdd(IdentityFamily.Fm, hash, "fm");

        Assert.Equal(first, second);
        Assert.Equal(first.DedupNumber, second.DedupNumber);
        Assert.Equal("fm:001", first.Canonical);
    }

    [Fact]
    public void GetOrAdd_DistinctHashes_AreMonotonicAndDeterministic()
    {
        var table = new DeterministicIdentityTable();
        var a = table.GetOrAdd(IdentityFamily.Fm, "hash-a", "fm");
        var b = table.GetOrAdd(IdentityFamily.Fm, "hash-b", "fm");
        var c = table.GetOrAdd(IdentityFamily.Fm, "hash-c", "fm");

        Assert.Equal(1, a.DedupNumber);
        Assert.Equal(2, b.DedupNumber);
        Assert.Equal(3, c.DedupNumber);

        // Recomputing from an empty table yields identical numbers (determinism).
        var fresh = new DeterministicIdentityTable();
        Assert.Equal(a, fresh.GetOrAdd(IdentityFamily.Fm, "hash-a", "fm"));
        Assert.Equal(b, fresh.GetOrAdd(IdentityFamily.Fm, "hash-b", "fm"));
    }

    [Fact]
    public void GetOrAdd_NumberSpaceSharedAcrossChips()
    {
        var table = new DeterministicIdentityTable();
        // FM 001, then a shared rhythm patch claims 002, then another FM is 003:
        // the number space is global, not per-family.
        Assert.Equal("fm:001", table.GetOrAdd(IdentityFamily.Fm, "fm-hash-1", "fm").Canonical);
        Assert.Equal("rhythm:002", table.GetOrAdd(IdentityFamily.Rhythm, "rh-hash", "rhythm").Canonical);
        Assert.Equal("fm:003", table.GetOrAdd(IdentityFamily.Fm, "fm-hash-2", "fm").Canonical);
    }

    [Fact]
    public void GetOrAdd_SameNormalizedPatch_AcrossChipAndChannel_IsOneIdentity()
    {
        // A normalized patch shared by YM2608 and YM2612 dedupes to ONE number —
        // the whole point of the cross-chip canonical table.
        var table = new DeterministicIdentityTable();
        string patch = DeterministicIdentityTable.HashText("shared-patch");
        var on2608 = table.GetOrAdd(IdentityFamily.Fm, patch, "fm");
        var on2612 = table.GetOrAdd(IdentityFamily.Fm, patch, "fm");
        Assert.Equal(on2612, on2608);
        Assert.Equal(1, on2612.DedupNumber);
    }
}