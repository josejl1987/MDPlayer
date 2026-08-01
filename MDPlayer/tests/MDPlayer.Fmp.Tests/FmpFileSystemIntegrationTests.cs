using Fmp.Core.IO;
using Fmp.Core.Nise98;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public class FmpFileSystemIntegrationTests : IDisposable
{
    private readonly string _testDir;

    public FmpFileSystemIntegrationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "fmpfs_int_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch { }
    }

    [Fact]
    public void OviWithMixedCasePviBankLookup()
    {
        // Simulate: OVI requests "VOICE.PVI" but the actual file is "voice.pvi"
        File.WriteAllText(Path.Combine(_testDir, "voice.pvi"), "pvi bank data");

        var fs = new FmpFileSystem(new[] { _testDir });

        // Request with the OVI's expected case (uppercase)
        Assert.True(fs.TryReadFile(new DosPath("VOICE.PVI"), out var data, out var source));
        Assert.Equal("voice.pvi", source.RequestedName, ignoreCase: true);
        Assert.EndsWith("voice.pvi", source.ResolvedPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("pvi bank data", System.Text.Encoding.ASCII.GetString(data.ToArray()));

        // SHA-256 was computed
        Assert.NotNull(source.Sha256);
        Assert.NotEmpty(source.Sha256);
    }

    [Fact]
    public void NiseDosAcceptsFmpFileSystem()
    {
        // Verify NiseDos can be constructed with IFmpFileSystem
        // and the DOS file resolution paths compile correctly
        var ft = new fileTemp();
        var regs = new Register286();
        var mem = new Memory98(64 * 1024);
        var fs = new FmpFileSystem(new[] { _testDir });

        // This should not throw — constructor stores the IFmpFileSystem
        var dos = new NiseDos(regs, mem, ft, fs);
        Assert.NotNull(dos);
    }

    [Fact]
    public void FmpFileSystemResolvesDifferentCasings()
    {
        // File with mixed case
        File.WriteAllText(Path.Combine(_testDir, "MyVoice.PVI"), "case test");

        var fs = new FmpFileSystem(new[] { _testDir });

        // Request with different case patterns
        Assert.True(fs.TryReadFile(new DosPath("MYVOICE.pvi"), out var data1, out _));
        Assert.Equal("case test", System.Text.Encoding.ASCII.GetString(data1.ToArray()));

        Assert.True(fs.TryReadFile(new DosPath("myvoice.PVI"), out var data2, out _));
        Assert.Equal("case test", System.Text.Encoding.ASCII.GetString(data2.ToArray()));

        Assert.True(fs.TryReadFile(new DosPath("MyVoice.PVI"), out var data3, out _));
        Assert.Equal("case test", System.Text.Encoding.ASCII.GetString(data3.ToArray()));
    }

    [Fact]
    public void NiseDosLoadDataUsesSupplementalResolverBytes()
    {
        File.WriteAllBytes(Path.Combine(_testDir, "Voice.PVI"), [1, 2, 3, 4]);
        var fs = new FmpFileSystem(new[] { _testDir });
        var dos = new NiseDos(new Register286(), new Memory98(64 * 1024), new fileTemp(), fs);

        Assert.Equal([1, 2, 3, 4], dos.LoadData("VOICE.pvi"));
    }

    [Fact]
    public void NiseDosSeekUsesSignedCxDxAndReadsBeyondEofSafely()
    {
        byte[] content = [10, 20, 30, 40, 50];
        string path = Path.Combine(_testDir, "bank.pvi");
        File.WriteAllBytes(path, content);

        var regs = new Register286 { DS = 0x0200, DX = 0 };
        var mem = new Memory98(64 * 1024);
        byte[] name = System.Text.Encoding.ASCII.GetBytes("BANK.PVI\0");
        for (int i = 0; i < name.Length; i++)
            mem.PokeB(regs.DS_DX + i, name[i]);
        var dos = new NiseDos(regs, mem, new fileTemp(), new FmpFileSystem(new[] { _testDir }));

        regs.AH = 0x3D;
        InvokeInt21(dos);
        Assert.False(regs.CF);
        short handle = regs.AX;

        regs.BX = handle;
        regs.AL = 0;
        regs.CX = 1;
        regs.DX = 2;
        regs.AH = 0x42;
        InvokeInt21(dos);
        Assert.False(regs.CF);
        Assert.Equal(1, (ushort)regs.DX);
        Assert.Equal(2, (ushort)regs.AX);

        regs.DS = 0x0200;
        regs.DX = 0x0200;
        regs.CX = 4;
        regs.AH = 0x3F;
        InvokeInt21(dos);
        Assert.False(regs.CF);
        Assert.Equal(0, (ushort)regs.AX);

        regs.AL = 2;
        regs.CX = -1;
        regs.DX = -1;
        regs.AH = 0x42;
        InvokeInt21(dos);
        Assert.False(regs.CF);
        Assert.Equal(0, (ushort)regs.DX);
        Assert.Equal(content.Length - 1, (ushort)regs.AX);
    }

    private static void InvokeInt21(NiseDos dos) =>
        typeof(NiseDos).GetMethod("INT21",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(dos, null);
}
