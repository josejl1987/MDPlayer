using Fmp.Core.Playback.Opna;
using Xunit;

namespace MDPlayer.Fmp.Tests.Playback.Opna;

/// <summary>
/// Exercises the absolute master-clock contract
/// <see cref="IClockedOpnaDevice"/> through the native backend
/// (<see cref="NativeOpnaDevice"/>).
/// </summary>
public sealed class ClockedOpnaDeviceTests
{
    [Fact]
    public void Open_ExposesContractMembers()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using IClockedOpnaDevice device = NativeOpnaDevice.Open(44100);

        Assert.Equal(44100, device.OutputRateHz);
        Assert.Equal(0ul, device.MasterClock);
        Assert.False(device.IrqAsserted);
    }

    [Fact]
    public void Advance_Writes_Reads_Drains_Reset()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using IClockedOpnaDevice device = NativeOpnaDevice.Open(44100);

        foreach ((ulong clock, byte bank, byte register, byte value) in OpnaFurnaceTrace.Writes)
            device.WriteRegister(clock, bank, register, value);

        ulong target = OpnaNativeSession.MasterClockHz;
        ulong cursor = 0;
        var buffer = new short[48_000 * 2];
        int total = 0;
        while (cursor < target)
        {
            cursor += 2_000_000;
            if (cursor > target)
                cursor = target;
            device.AdvanceTo(cursor);
            total += device.DrainAudio(buffer, 48_000 - total);
        }

        Assert.Equal(44_100, total);
        Assert.Equal(target, device.MasterClock);
        _ = device.ReadStatus(device.MasterClock, 0);
        _ = device.ReadStatus(device.MasterClock, 1);

        device.ResetChip();
        Assert.Equal(0ul, device.MasterClock);
        Assert.Equal(0, device.DrainAudio(new short[64], 32));
    }

    [Fact]
    public void ClearAdpcmRam_AndDispose_DoNotThrow()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        var device = NativeOpnaDevice.Open(48000);
        device.AdvanceTo(144);
        device.ClearAdpcmRam(0x00);
        device.Dispose();
        device.Dispose(); // idempotent
    }

    [Fact]
    public void Open_UnsupportedRate_SurfacesOpnaUnsupportedRateException()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        Assert.Throws<OpnaUnsupportedRateException>(() => NativeOpnaDevice.Open(11025));
    }

    private static string RequireNativeLibrary()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "runtimes", "linux-x64", "native", OpnaNativeSession.NativeLibraryFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new Xunit.Sdk.XunitException(
            "Native OPNA library not built. Run: cmake -S native/MDPlayer.OpnaNative -B native/MDPlayer.OpnaNative/build && cmake --build native/MDPlayer.OpnaNative/build");
    }

    private static IDisposable SetNativeLibrary(string path)
    {
        string previous = Environment.GetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar);
        Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, path);
        return new RestoreEnv(OpnaNativeSession.NativeLibraryEnvVar, previous);
    }

    private sealed class RestoreEnv : IDisposable
    {
        private readonly string _name;
        private readonly string _previous;

        public RestoreEnv(string name, string previous)
        {
            _name = name;
            _previous = previous;
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
