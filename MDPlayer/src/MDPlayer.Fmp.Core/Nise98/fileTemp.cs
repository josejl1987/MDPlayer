// Portable replacement for MDPlayer's fileTemp.
// Provides a simple in-memory file store without Setting dependency.
using Fmp.Core.Nise98;

namespace Fmp.Core.Nise98
{
    public class fileTemp : IVirtualFileStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private string _tempDir;

        public fileTemp()
        {
            _tempDir = Path.GetTempPath();
        }

        public bool TryGetFile(string path, out byte[] data)
        {
            return _files.TryGetValue(path, out data);
        }

        public void AddFile(string path, byte[] data)
        {
            _files[path] = data;
        }

        public void RemoveFile(string path)
        {
            _files.Remove(path);
        }

        public void Clear()
        {
            _files.Clear();
        }

        public bool Exists(string path)
        {
            return _files.ContainsKey(path);
        }

        // Original API methods used by Nise98 code
        public bool ExistTemp(string name)
        {
            return _files.ContainsKey(name);
        }

        public byte[] ReadTemp(string name)
        {
            return _files.TryGetValue(name, out var data) ? data : null;
        }

        public void WriteTemp(string name, byte[] data)
        {
            _files[name] = data;
        }

        public void DeleteTemp(string name)
        {
            _files.Remove(name);
        }
    }
}
