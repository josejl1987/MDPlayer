namespace Fmp.Core.Nise98;

/// <summary>
/// Virtual file store for Nise98 DOS operations.
/// Provides a name-to-buffer mapping for files that exist in the emulated
/// DOS filesystem but are provided by the host (e.g. FMP.COM loaded from
/// the host filesystem, archive entries).
/// </summary>
public interface IVirtualFileStore
{
    bool TryGetFile(string path, out byte[] data);
    void AddFile(string path, byte[] data);
    void RemoveFile(string path);
    void Clear();
    bool Exists(string path);
}
