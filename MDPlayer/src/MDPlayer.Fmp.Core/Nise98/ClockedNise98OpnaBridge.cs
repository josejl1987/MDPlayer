using Fmp.Core.Playback.Opna;

namespace Fmp.Core.Nise98;

/// <summary>
/// Clocked Nise98 OPNA port bridge: attaches to the existing
/// <see cref="Nise98"/> YM2608 port-I/O boundary (via
/// <see cref="INise98OpnaPortHandler"/>) and routes every FM access to an
/// <see cref="IClockedOpnaDevice"/> at an absolute YM2608 master-clock
/// timestamp derived from the authoritative Nise286 cycle count.
///
/// Port mapping is the existing Nise98 mapping, exactly: within each FM port
/// family the low byte selects the operation —
///  * 0x88 = bank-0 status read / address latch write, 0x8a = bank-0 data
///  * 0x8c = bank-1 status read / address latch write, 0x8e = bank-1 data
///
/// Bank-0 and bank-1 address latches are independent (never one shared
/// latch). Status reads (0x88/0x8c) return the native device status register
/// directly (<see cref="IClockedOpnaDevice.ReadStatus"/>) — no cached
/// MDSound status, no synthesized timer bits. Data reads (0x8a/0x8e) follow
/// the real YM2608 register read-back: they return the last value written to
/// the latched register, with the 86-board pseudo-registers (0x0e = 0,
/// 0xff = board-present 0x01) matching the legacy Nise98 FMPortInport
/// semantics the FMP driver depends on during boot. Equal clock values keep
/// call order; the bridge never artificially increments the OPNA clock.
/// Unknown low-byte ports throw, matching the legacy default case.
/// </summary>
public sealed class ClockedNise98OpnaBridge : INise98OpnaPortHandler
{
    private readonly NiseOpnaClockMapper _mapper;
    private readonly IClockedOpnaDevice _device;

    private byte _bank0Latch;
    private byte _bank1Latch;

    // Last-written register values per bank (the real YM2608 data port
    // read-back; 86-board pseudo-registers handled in Read).
    private readonly byte[] _bank0Regs = new byte[256];
    private readonly byte[] _bank1Regs = new byte[256];

    public ClockedNise98OpnaBridge(NiseOpnaClockMapper mapper, IClockedOpnaDevice device)
    {
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <summary>Current bank-0 address latch value.</summary>
    public byte Bank0Latch => _bank0Latch;

    /// <summary>Current bank-1 address latch value.</summary>
    public byte Bank1Latch => _bank1Latch;

    /// <summary>Last value written to a bank-0 register (read-back state).</summary>
    public byte GetBank0Register(byte address) => _bank0Regs[address];

    /// <summary>Last value written to a bank-1 register (read-back state).</summary>
    public byte GetBank1Register(byte address) => _bank1Regs[address];

    /// <summary>
    /// Resets the port-side state (address latches and read-back registers).
    /// Never touches the native chip; never clears external ADPCM RAM.
    /// </summary>
    public void Reset()
    {
        _bank0Latch = 0;
        _bank1Latch = 0;
        Array.Clear(_bank0Regs, 0, _bank0Regs.Length);
        Array.Clear(_bank1Regs, 0, _bank1Regs.Length);
    }

    /// <inheritdoc />
    public byte Read(ulong cpuCycle, ushort port)
    {
        ulong opnaClock = _mapper.Map(cpuCycle);
        switch (port & 0xff)
        {
            case 0x88:
                return _device.ReadStatus(opnaClock, 0);
            case 0x8a:
                return ReadBack(_bank0Regs, _bank0Latch);
            case 0x8c:
                return _device.ReadStatus(opnaClock, 1);
            case 0x8e:
                return ReadBack(_bank1Regs, _bank1Latch);
            default:
                throw new NotImplementedException(string.Format("Request port:${0:X04}", port));
        }
    }

    /// <summary>
    /// 86-board register read-back semantics (matches the legacy Nise98
    /// FMPortInport): 0x0e returns the IRQ-select byte (0), 0xff returns the
    /// board-present flag (0x01 for the 86/SpeakBoard), everything else
    /// returns the last value written to the register.
    /// </summary>
    private static byte ReadBack(byte[] regs, byte address)
    {
        return address switch
        {
            0x0e => 0x00,
            0xff => 0x01,
            _ => regs[address],
        };
    }

    /// <inheritdoc />
    public void Write(ulong cpuCycle, ushort port, byte data)
    {
        ulong opnaClock = _mapper.Map(cpuCycle);
        switch (port & 0xff)
        {
            case 0x88:
                _bank0Latch = data;
                break;
            case 0x8a:
                _bank0Regs[_bank0Latch] = data;
                _device.WriteRegister(opnaClock, 0, _bank0Latch, data);
                break;
            case 0x8c:
                _bank1Latch = data;
                break;
            case 0x8e:
                _bank1Regs[_bank1Latch] = data;
                _device.WriteRegister(opnaClock, 1, _bank1Latch, data);
                break;
            default:
                throw new NotImplementedException(string.Format("Request port:${0:X04}", port));
        }
    }
}