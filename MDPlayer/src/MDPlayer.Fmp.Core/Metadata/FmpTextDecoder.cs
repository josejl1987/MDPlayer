using System.Text;
using System.Text.RegularExpressions;

namespace Fmp.Core.Metadata;

/// <summary>
/// Centralized FMP text decoder. FMP metadata is traditionally CP932 (Shift-JIS),
/// but modern tools may emit UTF-8. This decoder applies a single, deterministic
/// policy used by every consumer (inspect, render, batch, visualization):
///
/// <list type="number">
///   <item>Strip trailing NUL / EOF (0x1A) bytes.</item>
///   <item>Honor a UTF-8 BOM if present.</item>
///   <item>Try strict UTF-8 for bytes containing non-ASCII (>= 0x80).</item>
///   <item>Fall back to strict CP932.</item>
///   <item>As a last resort, decode as CP932 with replacement characters for
///     malformed source bytes — never Latin-1, which silently mojibakes
///     valid UTF-8 by accepting every byte sequence.</item>
/// </list>
/// </summary>
internal static class FmpTextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictCp932;
    private static readonly Encoding ReplacementCp932;

    static FmpTextDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        StrictCp932 = Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
        ReplacementCp932 = Encoding.GetEncoding(932);
    }

    /// <summary>
    /// Decodes FMP comment/title bytes to a string using the centralized policy.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        // Strip trailing NUL / EOF markers.
        while (!bytes.IsEmpty && bytes[^1] is 0 or 0x1A)
            bytes = bytes[..^1];

        if (bytes.IsEmpty)
            return string.Empty;

        // UTF-8 BOM.
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return StrictUtf8.GetString(bytes[3..]).Normalize(NormalizationForm.FormC);

        // Only attempt strict UTF-8 when non-ASCII bytes are present.
        bool containsNonAscii = bytes.IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0;
        if (containsNonAscii)
        {
            try
            {
                return StrictUtf8.GetString(bytes).Normalize(NormalizationForm.FormC);
            }
            catch (DecoderFallbackException)
            {
                // FMP metadata is traditionally CP932. Fall through.
            }
        }

        try
        {
            return StrictCp932.GetString(bytes).Normalize(NormalizationForm.FormC);
        }
        catch (DecoderFallbackException)
        {
            return ReplacementCp932.GetString(bytes).Normalize(NormalizationForm.FormC);
        }
    }
}
