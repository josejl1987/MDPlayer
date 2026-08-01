using Fmp.Core.Nise98;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public class Nise98ComTests
{
    /// <summary>
    /// Boot a trivial COM file through Nise98, execute it, and inspect registers.
    /// The COM sets AX=0x1234 then calls INT 20h (DOS terminate).
    /// </summary>
    [Fact]
    public void BootAndRunTrivialCom()
    {
        // Arrange
        var ft = new fileTemp();
        var nise98 = new Nise98();

        string testComPath = Path.GetFullPath("testfixtures/test.com");
        Assert.True(File.Exists(testComPath), $"COM fixture not found at {testComPath}");

        // Initialize Nise98 with no-op callbacks (no YM2608 writes, no messages)
        nise98.Init(
            msgWrite: (msg, args) => { },
            opnaWrite: (dat) => { },
            fileTemp: ft,
            ongen: Nise98.enmOngenBoardType.PC9801_86B
        );

        // Load and run the COM file with a generous instruction budget
        int result = nise98.LoadRun(testComPath, "", 0x3000, false, true, false, 10_000_000);

        // Assert: LoadRun returns 0 on success
        Assert.Equal(0, result);

        // Check registers after execution
        var regs = nise98.GetRegisters();
        Assert.NotNull(regs);

        // AX should be 0x1234 (the value set by mov ax, 0x1234)
        Assert.Equal(0x1234, regs.AX);
    }

    /// <summary>
    /// Test that segmented memory can be written and read correctly via Peek/Poke.
    /// </summary>
    [Fact]
    public void SegmentedMemoryAddressing()
    {
        var mem = new Memory98(64 * 1024);

        // Write a pattern at segment 0x1000, offset 0x0100
        // Physical address = (segment << 4) + offset = 0x10000 + 0x0100 = 0x10100
        mem.PokeB(0x10100, 0xAB);
        mem.PokeB(0x10101, 0xCD);
        mem.PokeB(0x10102, 0xEF);

        // Read it back
        Assert.Equal(0xAB, mem.PeekB(0x10100));
        Assert.Equal(0xCD, mem.PeekB(0x10101));
        Assert.Equal(0xEF, mem.PeekB(0x10102));
    }

    /// <summary>
    /// Test that register operations work correctly.
    /// </summary>
    [Fact]
    public void RegisterOperations()
    {
        var regs = new Register286();

        // Set AX and verify
        regs.AX = 0x1234;
        Assert.Equal(0x1234, regs.AX);

        // Set BX and verify
        regs.BX = 0x5678;
        Assert.Equal(0x5678, regs.BX);

        // Set CS:IP
        regs.CS = 0x1000;
        regs.IP = 0x0100;
        Assert.Equal(0x1000, regs.CS);
        Assert.Equal(0x0100, regs.IP);
    }
}
