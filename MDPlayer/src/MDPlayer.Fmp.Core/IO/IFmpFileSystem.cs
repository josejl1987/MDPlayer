namespace Fmp.Core.IO;

/// <summary>
/// Provides DOS-compatible file system access for the emulated FM driver.
/// Maps DOS file names to host file data with case-insensitive lookup,
/// search-path precedence, and access restriction.
/// </summary>
public interface IFmpFileSystem
{
    bool TryReadFile(DosPath path, out ReadOnlyMemory<byte> data, out ResolvedFmpFile source);
}

/// <summary>
/// A file path in the emulated DOS environment.
/// Uses backslash separators, case-insensitive matching, and 8.3 short names where applicable.
/// </summary>
public readonly record struct DosPath(string Value)
{
    public string Value { get; } = Value.Replace('/', '\\');
    public override string ToString() => Value;
}

/// <summary>
/// Records how a file was resolved for diagnostic purposes.
/// </summary>
public readonly record struct ResolvedFmpFile(
    string RequestedName,
    string ResolvedPath,
    string Sha256);
