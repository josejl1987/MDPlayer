namespace Fmp.Core.PlaybackAssets.Furnace;

/// <summary>
/// Byte-level layout constants for the Furnace TFI (FM instrument) format:
/// a raw, headerless, fixed-size 42-byte binary file containing one
/// four-operator OPN FM instrument.
/// </summary>
internal static class TfiFormat
{
    public const int FileSize = 42;
    public const int GlobalSize = 2;
    public const int OperatorSize = 10;
    public const int OperatorCount = 4;
}
