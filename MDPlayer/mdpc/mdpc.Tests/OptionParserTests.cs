using System.Reflection;
using Xunit;

namespace mdpc.Tests
{
    public class OptionParserTests
    {
        private static void Analyze(mdpc target, string[] args)
        {
            MethodInfo method = typeof(mdpc).GetMethod("AnalyzeOption", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(target, new object[] { args });
        }

        [Fact]
        public void ReferenceOption_EnablesReferenceMode()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            Analyze(target, new[] { "--reference", "track.ovi" });
            Assert.True(GetField<bool>(target, "referenceMode"));
        }

        [Fact]
        public void ReferenceDirOption_ParsesDirectory()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            Analyze(target, new[] { "--reference-dir=C:\\ref", "track.ovi" });
            Assert.Equal("C:\\ref", GetField<string>(target, "referenceDir"));
        }

        [Fact]
        public void FmpComOption_ParsesPath()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            Analyze(target, new[] { "--fmp-com=C:\\FMP.COM", "track.ovi" });
            Assert.Equal("C:\\FMP.COM", GetField<string>(target, "fmpComPath"));
        }

        [Fact]
        public void LegacyEmuOption_SetsEmuOnly()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            Analyze(target, new[] { "-e", "track.ovi" });
            Assert.True(GetField<bool>(target, "emuOnly"));
        }

        [Fact]
        public void LegacyWaveOption_SetsWaveOut()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            Analyze(target, new[] { "-w", "track.ovi" });
            Assert.True(GetField<bool>(target, "waveout"));
        }

        [Fact]
        public void SearchPathOption_SingleString_AddsToList()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            Analyze(target, new[] { "--search-path=C:\\banks", "track.ovi" });
            var paths = GetField<List<string>>(target, "searchPaths");
            Assert.Contains("C:\\banks", paths);
        }

        [Fact]
        public void SearchPathOption_RepeatedI_AddsInOrder()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            Analyze(target, new[] { "-I", "./banks", "-I", "/home/jose/pvi", "track.ovi" });
            var paths = GetField<List<string>>(target, "searchPaths");
            Assert.Equal(2, paths.Count);
            Assert.Equal("./banks", paths[0]);
            Assert.Equal("/home/jose/pvi", paths[1]);
        }

        [Fact]
        public void SearchPathOption_Mixed_Accumulates()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            Analyze(target, new[] { "-I", "./banks", "--search-path=/extra", "track.ovi" });
            var paths = GetField<List<string>>(target, "searchPaths");
            Assert.Equal(2, paths.Count);
        }

        [Fact]
        public void TimeoutOption_ParsesSeconds()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            Analyze(target, new[] { "--timeout=30", "track.ovi" });
            Assert.Equal(30, GetField<int?>(target, "timeoutSeconds"));
        }

        [Fact]
        public void LegacyMaxDuration_ParsesTimeout()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            Analyze(target, new[] { "--max-duration=20", "track.ovi" });
            Assert.Equal(20, GetField<int?>(target, "timeoutSeconds"));
        }

        [Fact]
        public void MaxRenderDurationOption_ParsesSeconds()
        {
            var target = (mdpc)Activator.CreateInstance(typeof(mdpc), true);
            Analyze(target, new[] { "--max-render-duration=120", "track.ovi" });
            Assert.Equal(120, GetField<int?>(target, "maxRenderDurationSeconds"));
        }

        private static T GetField<T>(object target, string name)
        {
            FieldInfo field = typeof(mdpc).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            return (T)field.GetValue(target);
        }
    }
}
