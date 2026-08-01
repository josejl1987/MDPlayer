using System.Reflection;
using System.Security.Cryptography;
using Xunit;

namespace mdpc.Tests
{
    public class HelpersTests
    {
        [Fact]
        public void Sha256Hex_ComputesCorrectly()
        {
            string path = Path.Combine(Path.GetTempPath(), "sha256-test.bin");
            byte[] content = new byte[] { 0x00, 0x01, 0x02, 0x03 };
            File.WriteAllBytes(path, content);
            try
            {
                MethodInfo method = typeof(mdpc).GetMethod("Sha256Hex", 
                    BindingFlags.NonPublic | BindingFlags.Static);
                string result = (string)method.Invoke(null, new object[] { path });
                using var sha = SHA256.Create();
                string expected = Convert.ToHexString(sha.ComputeHash(content));
                Assert.Equal(expected, result);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Sha256Hex_MissingFile_Throws()
        {
            MethodInfo method = typeof(mdpc).GetMethod("Sha256Hex", 
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.Throws<TargetInvocationException>(() =>
                method.Invoke(null, new object[] { "/nonexistent/file.bin" }));
        }

        [Fact]
        public void MapExceptionToExitCode_MapsFileNotFound()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            MethodInfo method = typeof(mdpc).GetMethod("MapExceptionToExitCode", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            var fnf = new FileNotFoundException("FMP.COM not found", "FMP.COM");
            int code = (int)method.Invoke(target, new object[] { fnf });
            Assert.Equal(4, code);
        }

        [Fact]
        public void MapExceptionToExitCode_MapsUnsupported()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            MethodInfo method = typeof(mdpc).GetMethod("MapExceptionToExitCode", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            var ex = new InvalidOperationException("unsupported format");
            int code = (int)method.Invoke(target, new object[] { ex });
            Assert.Equal(3, code);
        }

        [Fact]
        public void MapExceptionToExitCode_DefaultsTo7()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            MethodInfo method = typeof(mdpc).GetMethod("MapExceptionToExitCode", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            var ex = new Exception("generic error");
            int code = (int)method.Invoke(target, new object[] { ex });
            Assert.Equal(7, code);
        }
    }
}
