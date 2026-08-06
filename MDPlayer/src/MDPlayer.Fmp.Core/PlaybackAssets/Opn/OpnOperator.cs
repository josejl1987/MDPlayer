namespace Fmp.Core.PlaybackAssets.Opn;

/// <summary>
/// Logical Yamaha FM operator 1..4. This numbering is the Yamaha logical
/// order and must not be confused with the TFI file operator order
/// (1,3,2,4), which is applied only at serialization time.
/// </summary>
internal enum OpnOperator
{
    Op1 = 0,
    Op2 = 1,
    Op3 = 2,
    Op4 = 3,
}
