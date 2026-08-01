using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fmp.Core.Metadata;

/// <summary>
/// Metadata extracted from an FMP track file and render session.
/// </summary>
internal class FmpMetadata
{
    static FmpMetadata()
    {
        // Ensure the code-page provider is registered even if FmpMetadata is
        // the first type touched; FmpTextDecoder registers it as well.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public string Title { get; set; } = "";
    public string Comment { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string SourceFormat { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public int SampleRate { get; set; } = 44100;
    public int Channels { get; set; } = 2;
    public int LoopCount { get; set; } = 2;
    public long RenderedSamples { get; set; }
    public string RenderedDuration { get; set; } = "";
    public string FadeDuration { get; set; } = "";
    public List<SupportFileInfo> SupportFiles { get; set; } = new();
    public string RendererVersion { get; set; } = "";
    public List<string> Warnings { get; set; } = new();

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public static FmpMetadata FromFmpFile(string path, int sampleRate = 44100, int loops = 2, double fadeSeconds = 5.0)
    {
        var meta = new FmpMetadata
        {
            SourceFile = Path.GetFileName(path),
            SourceFormat = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            SampleRate = sampleRate,
            LoopCount = loops,
            FadeDuration = $"00:00:{fadeSeconds:F0}",
            RendererVersion = GetRendererVersion()
        };

        // Compute source SHA-256
        try
        {
            using var fs = File.OpenRead(path);
            meta.SourceSha256 = Convert.ToHexString(SHA256.HashData(fs));
        }
        catch { }

        // Extract comment and title
        try
        {
            byte[] buf = File.ReadAllBytes(path);
            if (buf.Length >= 6)
            {
                int markerOffset = buf[0] + (buf[1] << 8);
                if (markerOffset + 4 <= buf.Length)
                {
                    string marker = Encoding.ASCII.GetString(buf, markerOffset, 3);
                    if (marker == "FMC")
                    {
                        int commentStart = markerOffset + 4;
                        int nullIdx = Array.IndexOf(buf, (byte)0, commentStart);
                        if (nullIdx < 0) nullIdx = buf.Length;
                        string rawComment = DecodeMetadata(buf.AsSpan(commentStart, nullIdx - commentStart));
                        meta.Comment = rawComment.Trim();
                        // Strip control characters for title (keep newlines/tabs for comment)
                        var lines = rawComment.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 0)
                        {
                            meta.Title = StripControlChars(lines[0].Trim());
                            if (string.IsNullOrEmpty(meta.Title))
                                meta.Title = Path.GetFileNameWithoutExtension(path);
                        }
                        else
                        {
                            meta.Title = Path.GetFileNameWithoutExtension(path);
                        }
                    }
                }
            }
        }
        catch { }

        if (string.IsNullOrEmpty(meta.Title))
            meta.Title = Path.GetFileNameWithoutExtension(path);

        return meta;
    }

    public string ToSafeFilename(char replace = '_')
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(Title.Select(c => invalid.Contains(c) ? replace : c));
    }

    internal static string DecodeMetadata(ReadOnlySpan<byte> bytes)
        => FmpTextDecoder.Decode(bytes);

    private static string StripControlChars(string s)
    {
        return new string(s.Where(c => !char.IsControl(c) || c == ' ' || c == '\t').ToArray());
    }

    private static string GetRendererVersion()
    {
        try
        {
            var asm = typeof(FmpMetadata).Assembly.GetName();
            return $"{asm.Name} {asm.Version}";
        }
        catch { return "mdplayer-render"; }
    }
}

internal class SupportFileInfo
{
    public string RequestedName { get; set; } = "";
    public string ResolvedPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
}
