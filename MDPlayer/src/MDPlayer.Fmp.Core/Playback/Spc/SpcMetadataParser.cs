using System.Globalization;
using System.Text;

namespace Fmp.Core.Playback.Spc;

/// <summary>
/// Defensive ID666 metadata parser. The SPC ecosystem has multiple ID666
/// encodings and no robust version field, so decoding is defensive (§23.2):
/// UTF-8 → CP932 → Windows-1252 → replacement characters.
/// </summary>
internal static class SpcMetadataParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictCp932;
    private static readonly Encoding ReplacementCp932;
    private static readonly Encoding Windows1252;

    // (offset, length) for binary ID666 text fields.
    private static readonly (int Offset, int Length)[] TextFields =
    {
        (0x2E, 32), (0x4E, 32), (0x6E, 16), (0x7E, 32), (0xB1, 32),
    };
    private const int PlayLengthOffset = 0xA9;
    private const int FadeLengthOffset = 0xAC;
    private const int EmulatorOffset = 0xD2;
    private const int HasId666Offset = 0x23;

    static SpcMetadataParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        StrictCp932 = Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        ReplacementCp932 = Encoding.GetEncoding(932);
        try { Windows1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
        catch { Windows1252 = null; }
    }

    public static SpcMetadata Parse(ReadOnlySpan<byte> data, List<string> warnings)
    {
        bool hasId666 = data.Length > HasId666Offset && (data[HasId666Offset] == 30 || data[HasId666Offset] == 31);
        string[] text = new string[TextFields.Length];
        for (int i = 0; i < TextFields.Length; i++)
            text[i] = DecodeField(data, TextFields[i].Offset, TextFields[i].Length);

        return new SpcMetadata
        {
            HasId666 = hasId666,
            SongTitle = text[0],
            GameTitle = text[1],
            Dumper = text[2],
            Comment = text[3],
            Artist = text[4],
            PlayLengthSeconds = DecodeInt(data, PlayLengthOffset, 3),
            FadeLengthMilliseconds = DecodeInt(data, FadeLengthOffset, 5),
            Emulator = DecodeEmulator(data, EmulatorOffset),
        };
    }

    private static string DecodeField(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length <= 0 || offset + length > data.Length)
            return string.Empty;
        ReadOnlySpan<byte> raw = data.Slice(offset, length);
        while (!raw.IsEmpty && raw[^1] is 0 or 0x1A)
            raw = raw[..^1];
        if (raw.IsEmpty)
            return string.Empty;

        string result;
        // 1. Strict UTF-8 when non-ASCII bytes are present.
        if (raw.IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0 && TryDecode(StrictUtf8, raw, out result))
            return result;
        // 2. CP932, 3. Windows-1252, 4. replacement (final).
        if (TryDecode(StrictCp932, raw, out result))
            return result;
        if (Windows1252 != null && TryDecode(Windows1252, raw, out result))
            return result;
        return ReplacementCp932.GetString(raw).TrimEnd().Normalize(NormalizationForm.FormC);
    }

    private static bool TryDecode(Encoding enc, ReadOnlySpan<byte> raw, out string result)
    {
        try
        {
            result = enc.GetString(raw).TrimEnd().Normalize(NormalizationForm.FormC);
            return true;
        }
        catch (DecoderFallbackException)
        {
            result = null;
            return false;
        }
    }

    private static int? DecodeInt(ReadOnlySpan<byte> data, int offset, int length)
    {
        string text = ReadAsciiDigits(data, offset, length);
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value : null;
    }

    private static string ReadAsciiDigits(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length <= 0 || offset + length > data.Length)
            return string.Empty;
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            byte b = data[offset + i];
            if (b is >= (byte)'0' and <= (byte)'9') sb.Append((char)b);
            else if (b is not 0 and not 0x1A and not (byte)' ') break;
        }
        return sb.ToString();
    }

    private static string DecodeEmulator(ReadOnlySpan<byte> data, int offset) =>
        offset < 0 || offset >= data.Length ? "" : data[offset] switch { 1 => "ZSNES", 2 => "Snes9x", _ => "" };
}
