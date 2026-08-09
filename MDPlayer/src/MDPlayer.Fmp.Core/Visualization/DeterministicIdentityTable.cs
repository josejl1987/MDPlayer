using System.Security.Cryptography;
using System.Text;

namespace Fmp.Core.Visualization;

/// <summary>
/// Assigns global, monotonic, deterministic dedup numbers to canonical normalized
/// instrument identities. The number space is shared across ALL chips ("FM 007"
/// means the same patch regardless of chip/instance/channel). Determinism is
/// first-seen in source order: the first distinct normalized identity to arrive
/// wins the lowest number, and identical input always produces the same number.
/// </summary>
internal sealed class DeterministicIdentityTable
{
    private readonly Dictionary<string, int> _byHash = new(StringComparer.Ordinal);
    private int _nextNumber = 1;

    /// <summary>
    /// Returns the stable identity for a normalized identity: the same
    /// <paramref name="normalizedHash"/> always maps to the same dedicated number.
    /// The hash is the SHA-256 of the serialized normalized instrument bytes.
    /// </summary>
    public InstrumentIdentity GetOrAdd(IdentityFamily family, string normalizedHash, string prefix)
    {
        if (!_byHash.TryGetValue(normalizedHash, out int number))
        {
            number = _nextNumber++;
            _byHash.Add(normalizedHash, number);
        }
        string canonical = $"{prefix}:{number:000}";
        return new InstrumentIdentity(family, number, canonical);
    }

    /// <summary>The next number that would be assigned (1-based, post-increment).</summary>
    public int NextNumber => _nextNumber;

    public static string HashBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string HashText(string text) =>
        HashBytes(Encoding.UTF8.GetBytes(text));
}