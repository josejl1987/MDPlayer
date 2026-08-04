namespace Fmp.Core.Nise98;

/// <summary>
/// Optional replacement for the legacy YM2608 FM-port handling, attached at
/// the existing Nise98 port-I/O boundary.
///
/// When set on <see cref="Nise98"/>, the FM-port cases of the existing
/// <c>INPb</c>/<c>OUTPb</c> dispatch route to this handler instead of the
/// legacy fmStatus / opnaWrite path. The handler receives the authoritative
/// Nise286 cycle count at access time (<c>cpuCycle</c>), so the clocked path
/// timestamps every individual port access at the exact boundary where the
/// emulated CPU performs it — never later from a render loop.
///
/// The existing Nise98 YM2608 port mapping is used exactly as-is: the four
/// FM port families (0x088/0x188/0x288/0x388) are dispatched here, the
/// address latches follow <c>port &amp; 0xff</c> semantics (0x88 = bank 0
/// address, 0x8a = bank 0 data, 0x8c = bank 1 address, 0x8e = bank 1 data),
/// and all unknown ports keep their existing behavior. When null, the legacy
/// MDSound path is completely untouched.
/// </summary>
public interface INise98OpnaPortHandler
{
    /// <summary>Handles one FM port read at the given CPU cycle count.</summary>
    byte Read(ulong cpuCycle, ushort port);

    /// <summary>Handles one FM port write at the given CPU cycle count.</summary>
    void Write(ulong cpuCycle, ushort port, byte data);
}
