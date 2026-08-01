namespace Fmp.Core.IO;

/// <summary>
/// Paths to FMP runtime assets: the FMP.COM executable and the optional
/// PPZ8.COM (empty when PPZ8 is not used).
/// </summary>
public sealed record FmpRuntimeAssets(string FmpComPath, string Ppz8ComPath)
{
    public FmpRuntimeAssets(string fmpComPath) : this(fmpComPath, "") { }
}
