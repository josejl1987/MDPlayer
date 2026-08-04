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
///  * 0x88 = bank-0 address latch, 0x8a = bank-0 data
///  * 0x8c = bank-1 address latch, 0x8e = bank-1 data
///
/// Bank-0 and bank-1 address latches are independent (never one shared
/// latch). Status reads return the native device status register directly
/// (<see cref="IClockedOpnaDevice.ReadStatus"/>) — no cached MDSound status,
/// no synthesized timer bits, no register-echo/Int-byte synthesis. Equal
/// clock values keep call order (the device preserves call order for equal
/// clocks); the bridge never artificially increments the OPNA clock.
/// Unknown low-byte ports throw, matching the legacy default case.
/// </summary>
public sealed class ClockedNise98OpnaBridge : INise98OpnaPortHandler
{
    private readonly NiseOpnaClockMapper _mapper;
    private readonly IClockedOpnaDevice _device;

    private byte _bank0Latch;
    private byte _bank1Latch;

    public ClockedNise98OpnaBridge(NiseOpnaClockMapper mapper, IClockedOpnaDevice device)
    {
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <summary>Current bank-0 address latch value.</summary>
    public byte Bank0Latch => _bank0Latch;

    /// <summary>Current bank-1 address latch value.</summary>
    public byte Bank1Latch => _bank1Latch;

    /// <summary>
    /// Resets the port-side state (address latches). Never touches the native
    /// chip; never clears external ADPCM RAM.
    /// </summary>
    public void Reset()
    {
        _bank0Latch = 0;
        _bank1Latch = 0;
    }

    /// <inheritdoc />
    public byte Read(ulong cpuCycle, ushort port)
    {
        ulong opnaClock = _mapper.Map(cpuCycle);
        switch (port & 0xff)
        {
            case 0x88:
            case 0x8a:
                return _device.ReadStatus(opnaClock, 0);
            case 0x8c:
            case 0x8e:
                return _device.ReadStatus(opnaClock, 1);
            default:
                throw new NotImplementedException(string.Format("Request port:${0:X04}", port));
        }
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
                _device.WriteRegister(opnaClock, 0, _bank0Latch, data);
                break;
            case 0x8c:
                _bank1Latch = data;
                break;
            case 0x8e:
                _device.WriteRegister(opnaClock, 1, _bank1Latch, data);
                break;
            default:
                throw new NotImplementedException(string.Format("Request port:${0:X04}", port));
        }
    }
}
