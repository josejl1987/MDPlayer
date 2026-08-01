using Fmp.Cli;
using Fmp.Core.Visualization;
using MDPlayer.Fmp.Tests.Fixtures;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace MDPlayer.Fmp.Tests;

public sealed class AnalysisIntegrationTests
{
    [Fact]
    public void AnalysisRunner_InvokesRealWorkerAndReusesValidCacheWithoutPython()
    {
        string python = Environment.GetEnvironmentVariable("MDPLAYER_ANALYSIS_PYTHON")
            ?? "/tmp/mdplayer-analysis-venv/bin/python";
        if (!File.Exists(python))
            throw SkipException.ForSkip($"analysis integration environment is unavailable: {python}");

        string root = Path.Combine(Path.GetTempPath(), "mdplayer-analysis-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string timelinePath = Path.Combine(root, "timeline.json");
            string outputDirectory = Path.Combine(root, "analysis");
            VisualizationJsonWriter.Write(timelinePath, VisualizationTimelineFixture.Create());

            var options = new AnalyzeOptions
            {
                Timeline = timelinePath,
                AnalysisOutput = outputDirectory,
                AnalysisPython = python,
                Detail = AnalysisDetail.Minimal,
                Force = true,
                TimeoutMinutes = 2,
            };

            Assert.Equal(0, AnalysisRunner.Run(options));
            string outputPath = Path.Combine(outputDirectory, "analysis.json");
            string cachePath = Path.Combine(outputDirectory, "cache-metadata.json");
            Assert.True(File.Exists(outputPath));
            Assert.True(File.Exists(cachePath));
            string firstOutput = File.ReadAllText(outputPath);
            using (JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(cachePath)))
            {
                Assert.Equal("sha256:", metadata.RootElement.GetProperty("inputHash").GetString()[..7]);
                Assert.Equal("sha256:", metadata.RootElement.GetProperty("outputHash").GetString()[..7]);
                Assert.Equal("minimal", metadata.RootElement.GetProperty("analysisDetail").GetString());
            }

            Thread.Sleep(50);
            options.Force = false;
            options.AnalysisPython = Path.Combine(root, "python-does-not-exist");
            Assert.Equal(0, AnalysisRunner.Run(options));
            Assert.Equal(firstOutput, File.ReadAllText(outputPath));

            // A truncated result, a bad output digest, a version mismatch, and
            // a detail mismatch are all cache misses. They must attempt the
            // worker only after the cache validator rejects the entry.
            string validMetadata = File.ReadAllText(cachePath);
            File.WriteAllText(outputPath, "{\n");
            Assert.Throws<InvalidOperationException>(() => AnalysisRunner.Run(options));
            File.WriteAllText(outputPath, firstOutput);

            File.WriteAllText(cachePath, validMetadata.Replace(
                "\"outputHash\": \"sha256:", "\"outputHash\": \"sha256:bad"));
            Assert.Throws<InvalidOperationException>(() => AnalysisRunner.Run(options));
            File.WriteAllText(cachePath, validMetadata);

            File.WriteAllText(cachePath, validMetadata.Replace(
                "\"workerVersion\": \"1.0.2\"", "\"workerVersion\": \"old-worker\""));
            Assert.Throws<InvalidOperationException>(() => AnalysisRunner.Run(options));
            File.WriteAllText(cachePath, validMetadata);

            options.Detail = AnalysisDetail.Standard;
            Assert.Throws<InvalidOperationException>(() => AnalysisRunner.Run(options));
            options.Detail = AnalysisDetail.Minimal;
            options.MaxDuration = 1;
            Assert.Equal(0, AnalysisRunner.Run(options));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnalysisRunner_DoesNotOverwriteExistingResultWhenWorkerFails()
    {
        string python = Environment.GetEnvironmentVariable("MDPLAYER_ANALYSIS_PYTHON")
            ?? "/tmp/mdplayer-analysis-venv/bin/python";
        if (!File.Exists(python))
            throw SkipException.ForSkip($"analysis integration environment is unavailable: {python}");
        if (!OperatingSystem.IsLinux())
            throw SkipException.ForSkip("wrapper worker fixture requires Linux");

        string root = Path.Combine(Path.GetTempPath(), "mdplayer-analysis-worker-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string timelinePath = Path.Combine(root, "timeline.json");
            string outputDirectory = Path.Combine(root, "analysis");
            VisualizationJsonWriter.Write(timelinePath, VisualizationTimelineFixture.Create());
            var options = new AnalyzeOptions
            {
                Timeline = timelinePath,
                AnalysisOutput = outputDirectory,
                AnalysisPython = python,
                Detail = AnalysisDetail.Minimal,
                Force = true,
                TimeoutMinutes = 2,
            };
            Assert.Equal(0, AnalysisRunner.Run(options));
            string outputPath = Path.Combine(outputDirectory, "analysis.json");
            string metadataPath = Path.Combine(outputDirectory, "cache-metadata.json");
            string oldOutput = File.ReadAllText(outputPath);
            string oldMetadata = File.ReadAllText(metadataPath);

            string wrapper = Path.Combine(root, "python-wrapper.sh");
            File.WriteAllText(wrapper,
                "#!/bin/sh\n"
                + "if [ \"$1\" = \"-c\" ]; then exec \"" + python + "\" \"$@\"; fi\n"
                + "exit 99\n");
            File.SetUnixFileMode(wrapper,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.AnalysisPython = wrapper;
            options.Force = true;

            Assert.Throws<InvalidOperationException>(() => AnalysisRunner.Run(options));
            Assert.Equal(oldOutput, File.ReadAllText(outputPath));
            Assert.Equal(oldMetadata, File.ReadAllText(metadataPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
