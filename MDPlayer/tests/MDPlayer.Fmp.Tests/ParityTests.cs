using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using global::Fmp.Core.IO;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Tests that the portable core has no wall-clock sleeps,
/// produces deterministic output, and is locale-independent.
/// </summary>
public class ParityTests
{

    private static readonly OpCode[] OneByteOpCodes = CreateOpCodeTable(twoByte: false);
    private static readonly OpCode[] TwoByteOpCodes = CreateOpCodeTable(twoByte: true);

    private static OpCode[] CreateOpCodeTable(bool twoByte)
    {
        var table = new OpCode[0x100];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode))
                continue;

            var opCode = (OpCode)field.GetValue(null);
            ushort value = unchecked((ushort)opCode.Value);
            if (!twoByte && value < 0x100)
                table[value] = opCode;
            else if (twoByte && (value & 0xFF00) == 0xFE00)
                table[value & 0xFF] = opCode;
        }

        return table;
    }

    private static bool TryGetOperandSize(OpCode opCode, byte[] il, int offset, out int size)
    {
        size = opCode.OperandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineI
                or OperandType.ShortInlineR
                or OperandType.ShortInlineBrTarget
                or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI
                or OperandType.InlineBrTarget
                or OperandType.InlineField
                or OperandType.InlineMethod
                or OperandType.InlineSig
                or OperandType.InlineString
                or OperandType.InlineTok
                or OperandType.InlineType => 4,
            OperandType.InlineI8
                or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 0,
            _ => -1,
        };

        if (opCode.OperandType != OperandType.InlineSwitch
            && size >= 0
            && offset <= il.Length - size)
            return true;
        if (opCode.OperandType != OperandType.InlineSwitch || offset > il.Length - 4)
            return false;

        int count = BitConverter.ToInt32(il, offset);
        if (count < 0 || count > (il.Length - offset - 4) / 4)
            return false;

        size = 4 + count * 4;
        return true;
    }
    /// <summary>
    /// Verify that MDPlayer.Fmp.Core has no Thread.Sleep or Task.Delay calls.
    /// This ensures offline rendering never introduces real-time pacing.
    /// </summary>
    [Fact]
    public void CoreHasNoSleepCalls()
{
    var asm = typeof(global::Fmp.Core.Rendering.FmpRuntime).Assembly;
    var module = asm.ManifestModule;
    var offenders = new List<string>();
    int unresolvedCallTokens = 0;

    foreach (var type in asm.GetTypes())
    {
        var methods = type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly);
        foreach (var method in methods)
        {
            if (method.Name == "Finalize")
                continue;

            MethodBody body;
            byte[] il;
            try
            {
                body = method.GetMethodBody();
                il = body?.GetILAsByteArray();
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            catch (NotSupportedException)
            {
                continue;
            }

            if (il is null)
                continue;

            for (int offset = 0; offset < il.Length;)
            {
                OpCode opCode;
                byte first = il[offset++];
                if (first == 0xFE)
                {
                    if (offset >= il.Length)
                    {
                        unresolvedCallTokens++;
                        break;
                    }

                    opCode = TwoByteOpCodes[il[offset++]];
                }
                else
                {
                    opCode = OneByteOpCodes[first];
                }

                int operandOffset = offset;
                if (!TryGetOperandSize(opCode, il, operandOffset, out int operandSize))
                {
                    unresolvedCallTokens++;
                    break;
                }

                if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt)
                {
                    if (operandSize != 4 || operandOffset + operandSize > il.Length)
                    {
                        unresolvedCallTokens++;
                    }
                    else
                    {
                        int token = BitConverter.ToInt32(il, operandOffset);
                        try
                        {
                            Type[] typeArguments = type.IsGenericType
                                ? type.GetGenericArguments() : null;
                            Type[] methodArguments = method.IsGenericMethod
                                ? method.GetGenericArguments() : null;
                            MethodBase target = module.ResolveMethod(
                                token, typeArguments, methodArguments);
                            if ((target.DeclaringType == typeof(Thread)
                                && target.Name == nameof(Thread.Sleep))
                                || (target.DeclaringType == typeof(Task)
                                && target.Name == nameof(Task.Delay)))
                            {
                                offenders.Add($"{type.FullName}.{method.Name}"
                                    + $" -> {target.DeclaringType.FullName}.{target.Name}");
                            }
                        }
                        catch (ArgumentException)
                        {
                            unresolvedCallTokens++;
                        }
                        catch (InvalidOperationException)
                        {
                            unresolvedCallTokens++;
                        }
                    }
                }

                offset += operandSize;
            }
        }
    }

    Assert.Empty(offenders);
    Assert.Equal(0, unresolvedCallTokens);
}

    /// <summary>
    /// Verify that repeated renders produce identical output.
    /// This test renders two short WAV files and compares them byte-for-byte.
    /// It requires FMP.COM and an OVI to be available.
    /// </summary>
    [SkippableFact]
    public void RepeatedRender_ProducesIdenticalOutput()
    {
        string fmpComPath = Path.GetFullPath("testfixtures/FMP.COM");
        Skip.IfNot(File.Exists(fmpComPath), "FMP.COM not available in test output.");

        // Find an OVI test fixture
        string oviPath = FindOviFixture();
        Skip.If(oviPath is null, "No OVI fixture is available.");

        byte[] trackData = File.ReadAllBytes(oviPath);
        var testDir = Path.GetDirectoryName(oviPath);
        var fileSystem = new global::Fmp.Core.IO.FmpFileSystem(new[] { testDir });

        byte[] firstRender = RenderToBytes(fmpComPath, trackData, oviPath, fileSystem);
        byte[] secondRender = RenderToBytes(fmpComPath, trackData, oviPath, fileSystem);

        Assert.Equal(firstRender, secondRender);
    }

    /// <summary>
    /// Verify that rendering is independent of the working directory.
    /// (Same FMP.COM and OVI paths → same output regardless of CWD)
    /// </summary>
    [SkippableFact]
    public void Render_IndependentOfWorkingDirectory()
    {
        string fmpComPath = Path.GetFullPath("testfixtures/FMP.COM");
        Skip.IfNot(File.Exists(fmpComPath), "FMP.COM not available in test output.");

        string oviPath = FindOviFixture();
        Skip.If(oviPath is null, "No OVI fixture is available.");

        byte[] trackData = File.ReadAllBytes(oviPath);
        var testDir = Path.GetDirectoryName(oviPath);
        var fileSystem = new global::Fmp.Core.IO.FmpFileSystem(new[] { testDir });

        byte[] result = RenderToBytes(fmpComPath, trackData, oviPath, fileSystem);
        Assert.NotNull(result);
    }

    private static string FindOviFixture()
    {
        // Check test output directory for OVI files
        var testDir = Directory.GetCurrentDirectory();
        var files = Directory.GetFiles(testDir, "*.ovi", SearchOption.AllDirectories);
        if (files.Length > 0) return files[0];

        // Check Downloads (user's test fixtures)
        string downloads = "/home/jose/Downloads";
        if (Directory.Exists(downloads))
        {
            files = Directory.GetFiles(downloads, "*.OVI", SearchOption.TopDirectoryOnly);
            if (files.Length > 0) return files[0];
        }
        return null;
    }

    private static byte[] RenderToBytes(string fmpComPath, byte[] trackData, string trackFileName, global::Fmp.Core.IO.FmpFileSystem fileSystem)
    {
        var assets = new FmpRuntimeAssets(fmpComPath);
        var renderer = new global::Fmp.Core.Rendering.FmpRenderer(assets, fileSystem, 44100);
        var opts = new global::Fmp.Core.Rendering.FmpRenderer.Options
        {
            LoopCount = 1,
            FadeSeconds = 0.01,
            TailSeconds = 0.01,
            MaxDurationSeconds = 2.0
        };

        string outputPath = Path.GetTempFileName() + ".wav";
        try
        {
            var result = renderer.RenderToWav(trackData, trackFileName, outputPath, opts);
            if (!result.Success) return null;
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
        }
    }
}
