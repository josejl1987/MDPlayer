using System.Security.Cryptography;

namespace Fmp.Core.Visualization;

/// <summary>
/// A stable 32-byte SHA-256 content digest for a DAC sample payload. Value type
/// with deterministic equality/ordering so it can be used as a canonical key.
/// </summary>
internal readonly struct DacHash256 : IEquatable<DacHash256>, IComparable<DacHash256>
{
    private readonly byte[] _bytes; // 32 bytes; null not permitted for a valid hash

    public DacHash256(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != 32)
            throw new ArgumentException("SHA-256 digest must be exactly 32 bytes.", nameof(digest));
        _bytes = digest.ToArray();
    }

    public string Hex => _bytes is null ? "" : Convert.ToHexString(_bytes).ToLowerInvariant();

    public ReadOnlySpan<byte> Span => _bytes;

    public bool Equals(DacHash256 other)
        => _bytes is not null && other._bytes is not null && CryptographicOperations.FixedTimeEquals(_bytes, other._bytes);

    public override bool Equals(object? obj) => obj is DacHash256 other && Equals(other);

    public override int GetHashCode()
    {
        if (_bytes is null)
            return 0;
        // SHA-256 digests are uniformly distributed: hash the first 4 bytes.
        return BitConverter.ToInt32(_bytes, 0);
    }

    public int CompareTo(DacHash256 other)
    {
        if (_bytes is null)
            return other._bytes is null ? 0 : -1;
        if (other._bytes is null)
            return 1;
        return _bytes.AsSpan().SequenceCompareTo(other._bytes);
    }

    public static bool operator ==(DacHash256 left, DacHash256 right) => left.Equals(right);
    public static bool operator !=(DacHash256 left, DacHash256 right) => !left.Equals(right);

    public override string ToString() => Hex;

    /// <summary>Computes the SHA-256 digest of a payload span.</summary>
    public static DacHash256 Of(ReadOnlySpan<byte> payload)
        => new(SHA256.HashData(payload));
}