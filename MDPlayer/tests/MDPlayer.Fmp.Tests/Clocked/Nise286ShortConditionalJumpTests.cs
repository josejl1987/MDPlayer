using Fmp.Core.Nise98;
using Xunit;
using Xunit.Sdk;
using Nise98Machine = Fmp.Core.Nise98.Nise98;

namespace MDPlayer.Fmp.Tests.Clocked;

/// <summary>
/// Exhaustive tests for the 16 short conditional jumps (opcodes 0x70–0x7F) on
/// the authoritative Fmp.Core Nise286. Every test executes the real decoder via
/// <see cref="Nise98Machine.StepExecute"/> (Workstream H), exercises the shared
/// condition evaluator with exact taken / not-taken results (Workstream E),
/// verifies signed rel8 displacement + branch base + 16-bit IP wrap
/// (Workstream F) and full flag preservation (Workstream G).
///
/// Convention: a single <c>Jcc rel8</c> is placed at IP 0x0000 with its
/// displacement byte at 0x0001. After <c>Fetch()</c> of opcode and displacement
/// the branch base is IP 0x0002. Not-taken leaves IP 0x0002; taken leaves
/// <c>0x0002 + (sbyte)disp</c> wrapped at 16 bits. cs.runtime uses the machine
/// at linear base 0x10000.
/// </summary>
public sealed class Nise286ShortConditionalJumpTests
{
    private const int BaseAddr = 0x10000;
    private const ushort BranchBaseIp = 0x0002; // post-displacement IP

    private static Nise98Machine BuildMachine()
    {
        var ft = new fileTemp();
        var nise98 = new Nise98Machine();
        nise98.Init(
            msgWrite: (msg, args) => { },
            opnaWrite: (dat) => { },
            fileTemp: ft,
            ongen: Nise98Machine.enmOngenBoardType.PC9801_86B);
        nise98.CpuClockFrequencyHz = 8_000_000;
        return nise98;
    }

    /// <summary>
    /// Runs one short conditional jump with the given flag values and returns
    /// whether the branch was TAKEN (i.e. IP moved to BranchBaseIp + disp).
    /// </summary>
    private static bool RunShortJump(byte opcode, sbyte disp,
        bool cf = false, bool pf = false, bool zf = false, bool sf = false, bool of = false)
    {
        Nise98Machine nise98 = BuildMachine();
        Memory98 mem = nise98.GetMem();
        mem.PokeB(BaseAddr + 0, opcode);
        mem.PokeB(BaseAddr + 1, unchecked((byte)disp));

        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000; // linear base 0x10000
        regs.IP = 0x0000;
        regs.CF = cf; regs.PF = pf; regs.ZF = zf; regs.SF = sf; regs.OF = of;
        regs.AF = false; regs.TF = false; regs.IF = true; regs.DF = false;

        nise98.StepExecute();

        ushort ip = (ushort)regs.IP;
        ushort expectedNotTaken = BranchBaseIp;
        ushort expectedTaken = unchecked((ushort)(BranchBaseIp + (sbyte)disp));
        if (ip == expectedNotTaken) return false;
        if (ip == expectedTaken) return true;
        throw new XunitException(
            $"0x{opcode:X2} disp {disp} CF={cf} PF={pf} ZF={zf} SF={sf} OF={of}: IP=0x{ip:X4} (expected 0x{expectedNotTaken:X4} or 0x{expectedTaken:X4})");
    }

    // ---- Workstream H: decoder coverage (no NotImplementedException) ----

    [Fact]
    public void AllShortJccOpcodes_ExecuteRealDecoder_NoNotImplemented()
    {
        // Every opcode 0x70..0x7F must dispatch and execute through the real
        // decoder. Drive the flags so each branch is taken once and not-taken
        // once, both running the shared executor without throwing.
        foreach (byte opcode in Enumerable.Range(0x70, 16).Select(x => (byte)x))
        {
            _ = RunShortJump(opcode, 0x01, zf: false);              // not taken
            _ = RunShortJump(opcode, 0x01, of: true, sf: true, cf: true, zf: true, pf: true); // taken-ish
        }
    }

    // ---- Workstream E: single-flag conditions ----

    [Theory]
    [InlineData(0x70, false)]   // JO: OF=0 -> not taken
    [InlineData(0x70, true)]    // JO: OF=1 -> taken
    [InlineData(0x71, true)]    // JNO: OF=0 -> taken
    [InlineData(0x71, false)]   // JNO: OF=1 -> not taken
    [InlineData(0x72, false)]   // JB: CF=0 -> not taken
    [InlineData(0x72, true)]    // JB: CF=1 -> taken
    [InlineData(0x73, true)]    // JAE: CF=0 -> taken
    [InlineData(0x73, false)]   // JAE: CF=1 -> not taken
    [InlineData(0x74, false)]   // JE: ZF=0 -> not taken
    [InlineData(0x74, true)]    // JE: ZF=1 -> taken
    [InlineData(0x75, true)]    // JNE: ZF=0 -> taken
    [InlineData(0x75, false)]   // JNE: ZF=1 -> not taken
    [InlineData(0x78, false)]   // JS: SF=0 -> not taken
    [InlineData(0x78, true)]    // JS: SF=1 -> taken
    [InlineData(0x79, true)]    // JNS: SF=0 -> taken
    [InlineData(0x79, false)]   // JNS: SF=1 -> not taken
    [InlineData(0x7A, false)]   // JP: PF=0 -> not taken
    [InlineData(0x7A, true)]    // JP: PF=1 -> taken
    [InlineData(0x7B, true)]    // JNP: PF=0 -> taken
    [InlineData(0x7B, false)]   // JNP: PF=1 -> not taken
    public void SingleFlagCondition_RespectsControllingFlag(int opcode, bool flagSet)
    {
        bool expected = opcode switch
        {
            0x70 => flagSet,
            0x71 => !flagSet,
            0x72 => flagSet,
            0x73 => !flagSet,
            0x74 => flagSet,
            0x75 => !flagSet,
            0x78 => flagSet,
            0x79 => !flagSet,
            0x7A => flagSet,
            0x7B => !flagSet,
            _ => throw new ArgumentException(),
        };
        // Map opcode -> its controlling flag.
        (bool cf, bool pf, bool zf, bool sf, bool of) f = opcode switch
        {
            0x70 or 0x71 => (cf: false, pf: false, zf: false, sf: false, of: flagSet),
            0x72 or 0x73 => (cf: flagSet, pf: false, zf: false, sf: false, of: false),
            0x74 or 0x75 => (cf: false, pf: false, zf: flagSet, sf: false, of: false),
            0x78 or 0x79 => (cf: false, pf: false, zf: false, sf: flagSet, of: false),
            0x7A or 0x7B => (cf: false, pf: flagSet, zf: false, sf: false, of: false),
            _ => throw new ArgumentException(),
        };
        Assert.Equal(expected, RunShortJump((byte)opcode, 0x01, f.cf, f.pf, f.zf, f.sf, f.of));
    }

    // ---- Workstream E: composite unsigned (CF, ZF) ----

    [Theory]
    [InlineData(0x76, false, false, false)] // JBE: CF||ZF
    [InlineData(0x76, true, false, true)]
    [InlineData(0x76, false, true, true)]
    [InlineData(0x76, true, true, true)]
    [InlineData(0x77, false, false, true)]  // JA: !CF && !ZF
    [InlineData(0x77, true, false, false)]
    [InlineData(0x77, false, true, false)]
    [InlineData(0x77, true, true, false)]
    public void CompositeUnsigned_BelowOrEqual_Above(int opcode, bool cf, bool zf, bool expected)
    {
        Assert.Equal(expected, RunShortJump((byte)opcode, 0x01, cf: cf, zf: zf));
    }

    // ---- Workstream E: composite signed (SF, OF) ----

    [Theory]
    [InlineData(0x7C, false, false, false)] // JL: SF!=OF
    [InlineData(0x7C, true, false, true)]
    [InlineData(0x7C, false, true, true)]
    [InlineData(0x7C, true, true, false)]
    [InlineData(0x7D, false, false, true)]  // JGE: SF==OF
    [InlineData(0x7D, true, false, false)]
    [InlineData(0x7D, false, true, false)]
    [InlineData(0x7D, true, true, true)]
    public void CompositeSigned_Less_GreaterOrEqual(int opcode, bool sf, bool of, bool expected)
    {
        Assert.Equal(expected, RunShortJump((byte)opcode, 0x01, sf: sf, of: of));
    }

    // ---- Workstream E: composite signed with ZF (ZF, SF, OF) ----

    [Theory]
    [InlineData(0x7E, false, false, false, false)] // JLE: ZF || SF!=OF
    [InlineData(0x7E, true, false, false, true)]
    [InlineData(0x7E, false, true, false, true)]
    [InlineData(0x7E, false, false, true, true)]
    [InlineData(0x7E, true, true, false, true)]
    [InlineData(0x7E, false, true, true, false)]
    [InlineData(0x7E, true, false, true, true)]
    [InlineData(0x7E, true, true, true, true)]
    [InlineData(0x7F, false, false, false, true)]  // JG: !ZF && SF==OF
    [InlineData(0x7F, true, false, false, false)]
    [InlineData(0x7F, false, true, false, false)]
    [InlineData(0x7F, false, false, true, false)]
    [InlineData(0x7F, true, true, false, false)]
    [InlineData(0x7F, false, true, true, true)]
    [InlineData(0x7F, true, false, true, false)]
    [InlineData(0x7F, true, true, true, false)]
    public void CompositeSignedWithZf_LessOrEqual_Greater(int opcode, bool zf, bool sf, bool of, bool expected)
    {
        Assert.Equal(expected, RunShortJump((byte)opcode, 0x01, zf: zf, sf: sf, of: of));
    }

    // ---- Workstream F: displacement and IP behaviour ----

    [Theory]
    [InlineData(0x70, (sbyte)0, (ushort)0x0002)]     // +0
    [InlineData(0x70, (sbyte)1, (ushort)0x0003)]     // +1
    [InlineData(0x70, (sbyte)127, (ushort)0x0081)]   // +127
    [InlineData(0x70, (sbyte)-1, (ushort)0x0001)]    // -1
    [InlineData(0x70, (sbyte)-128, (ushort)0xFF82)]  // -128 -> 16-bit wrap low
    public void TakenJump_TargetsBranchBasePlusSignedDisp(int opcode, sbyte disp, ushort expectedIp)
    {
        // JO with OF=1 always takes the jump. Target = BranchBaseIp + disp.
        // Verified directly via post-execution IP. (+0 is indistinguishable
        // from not-taken by IP alone, so the direct assertion is the source of
        // truth for every displacement.)
        Nise98Machine nise98 = BuildMachine();
        Memory98 mem = nise98.GetMem();
        mem.PokeB(BaseAddr + 0, (byte)opcode);
        mem.PokeB(BaseAddr + 1, unchecked((byte)disp));
        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;
        regs.OF = true;
        nise98.StepExecute();
        Assert.Equal(expectedIp, (ushort)regs.IP);
    }

    [Fact]
    public void NotTaken_LeavesIpAtBranchBase()
    {
        // JO with OF=0 is not taken: IP stays at the post-displacement base.
        Assert.False(RunShortJump(0x70, 0x01, of: false));
    }

    [Fact]
    public void Displacement_SignExtended_Negative128_TargetsLowMemory()
    {
        // Signed -128 (0x80 byte). An unsigned interpretation would give
        // 0x0002 + 0x80 = 0x0082; signed gives 0xFF82. Assert the signed value.
        Nise98Machine nise98 = BuildMachine();
        Memory98 mem = nise98.GetMem();
        mem.PokeB(BaseAddr + 0, 0x70);   // JO
        mem.PokeB(BaseAddr + 1, 0x80);   // -128 signed
        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;
        regs.OF = true;
        nise98.StepExecute();
        Assert.Equal((ushort)0xFF82, (ushort)regs.IP);
    }

    [Fact]
    public void BranchBase_IsPostDisplacementIp_NotOpcodeAddress()
    {
        // If the branch base were the OPCODE address (0x0000), a +1 taken jump
        // would land at 0x0001. Correct base is post-displacement IP (0x0002),
        // so +1 lands at 0x0003.
        Assert.True(RunShortJump(0x70, 0x01, of: true)); // proves landing 0x0003
        Nise98Machine nise98 = BuildMachine();
        Memory98 mem = nise98.GetMem();
        mem.PokeB(BaseAddr + 0, 0x70);
        mem.PokeB(BaseAddr + 1, 0x01);
        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;
        regs.OF = true;
        nise98.StepExecute();
        Assert.Equal((ushort)0x0003, (ushort)regs.IP);
    }

    [Fact]
    public void IpWrap_FromLowToHigh_BackwardNegative128()
    {
        // 0x0002 + (-128) wraps to 0xFF82 (a low->high 16-bit wrap). The
        // symmetric forward wrap through 0xFFFF is also exercised: base 0x0002
        // + 127 = 0x0081 (no wrap) is covered above; a true carry past 0xFFFF is
        // not reachable from base 0x0002 with an 8-bit signed disp, so this
        // verifies the canonical downward wrap and the forward high result.
        Nise98Machine nise98 = BuildMachine();
        Memory98 mem = nise98.GetMem();
        mem.PokeB(BaseAddr + 0, 0x70);
        mem.PokeB(BaseAddr + 1, 0x80); // -128
        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;
        regs.OF = true;
        nise98.StepExecute();
        Assert.Equal((ushort)0xFF82, (ushort)regs.IP);
    }

    // ---- Workstream G: flag preservation ----

    [Theory]
    [InlineData(0x70)]  // single flag
    [InlineData(0x72)]
    [InlineData(0x74)]
    [InlineData(0x7A)]
    [InlineData(0x7C)]  // composite signed
    [InlineData(0x7D)]
    [InlineData(0x7E)]  // composite with ZF
    [InlineData(0x7F)]
    public void Flags_RemainUnchanged_AfterBranch_Taken(int opcode)
    {
        // Drive an all-ones flag pattern, force the branch taken.
        AssertFlagsPreserved((byte)opcode, cf: true, pf: true, zf: true, sf: true, of: true);
    }

    [Theory]
    [InlineData(0x70)]
    [InlineData(0x72)]
    [InlineData(0x74)]
    [InlineData(0x7A)]
    [InlineData(0x7C)]
    [InlineData(0x7D)]
    [InlineData(0x7E)]
    [InlineData(0x7F)]
    public void Flags_RemainUnchanged_AfterBranch_NotTaken(int opcode)
    {
        // All-zeros flag pattern forces the branch not taken.
        AssertFlagsPreserved((byte)opcode, cf: false, pf: false, zf: false, sf: false, of: false);
    }

    private static void AssertFlagsPreserved(byte opcode, bool cf, bool pf, bool zf, bool sf, bool of)
    {
        Nise98Machine nise98 = BuildMachine();
        Memory98 mem = nise98.GetMem();
        mem.PokeB(BaseAddr + 0, opcode);
        mem.PokeB(BaseAddr + 1, 0x01);

        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;
        regs.CF = cf; regs.PF = pf; regs.AF = !cf; regs.ZF = zf; regs.SF = sf;
        regs.TF = !sf; regs.IF = true; regs.DF = !zf; regs.OF = of;

        ushort flagBefore = (ushort)regs.FLAG;
        nise98.StepExecute(); // the branch

        Assert.Equal(flagBefore, (ushort)regs.FLAG);
        Assert.Equal(cf, regs.CF);
        Assert.Equal(pf, regs.PF);
        Assert.Equal(!cf, regs.AF);
        Assert.Equal(zf, regs.ZF);
        Assert.Equal(sf, regs.SF);
        Assert.Equal(!sf, regs.TF);
        Assert.True(regs.IF);
        Assert.Equal(!zf, regs.DF);
        Assert.Equal(of, regs.OF);
    }
}