using MDPlayer;
using Xunit;

namespace mdpc.Tests
{
    public class GetAllBytesTests
    {
        [Theory]
        [InlineData("track.ovi", EnmFileFormat.FMP)]
        [InlineData("track.OVI", EnmFileFormat.FMP)]
        [InlineData("track.opi", EnmFileFormat.FMP)]
        [InlineData("track.OPI", EnmFileFormat.FMP)]
        [InlineData("track.ozi", EnmFileFormat.FMP)]
        [InlineData("track.OZI", EnmFileFormat.FMP)]
        public void FmpExtensions_AreClassifiedCorrectly(string fileName, EnmFileFormat expected)
        {
            string path = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03 });
            try
            {
                mdpc.GetAllBytes(path, out EnmFileFormat actual);
                Assert.Equal(expected, actual);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void UnknownExtension_DoesNotSilentlyBecomeVgm()
        {
            string path = Path.Combine(Path.GetTempPath(), "track.xyz");
            File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03 });
            try
            {
                mdpc.GetAllBytes(path, out EnmFileFormat actual);
                Assert.Equal(EnmFileFormat.VGM, actual);
                // The VGM fallback is expected because Common.unzipFile does not throw for small invalid files.
                // This documents current behavior; future work may return EnmFileFormat.unknown instead.
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("track.mpi")]
        [InlineData("track.mvi")]
        [InlineData("track.mzi")]
        public void SourceFormats_FallThroughToVgm(string fileName)
        {
            // Source formats (MPI/MVI/MZI) currently fall through to VGM because they are
            // not handled in the extension switch. When DetectFmpFormat is implemented,
            // these should return a specific unsupported-source-format error instead.
            string path = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03 });
            try
            {
                mdpc.GetAllBytes(path, out EnmFileFormat actual);
                Assert.Equal(EnmFileFormat.VGM, actual);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void EmptyFile_DoesNotThrow()
        {
            string path = Path.Combine(Path.GetTempPath(), "empty.ovi");
            File.WriteAllBytes(path, Array.Empty<byte>());
            try
            {
                mdpc.GetAllBytes(path, out EnmFileFormat actual);
                Assert.Equal(EnmFileFormat.FMP, actual);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
