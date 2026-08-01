using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Application.Projects;
using Xunit;

namespace Fmp.Application.Tests;

public class ProjectStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsRequest()
    {
        string directory = CreateTempDir();
        try
        {
            string projectPath = Path.Combine(directory, "song.mdpviz.json");
            VisualizationProject saved = VisualizationProjectStore.Save(
                new VisualizationProject { Request = TestRequests.FullyPopulated() }, projectPath);

            Assert.True(File.Exists(projectPath));
            Assert.NotNull(saved.FilePath);

            VisualizationProject loaded = VisualizationProjectStore.Load(projectPath);
            Assert.Equal(
                VisualizationRequestSerializer.Serialize(TestRequests.FullyPopulated()),
                VisualizationRequestSerializer.Serialize(loaded.Request));
            Assert.Equal(projectPath, loaded.FilePath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RelativeInputPath_IsUsedWhenSharedRoot()
    {
        string directory = CreateTempDir();
        try
        {
            string input = Path.Combine(directory, "nested", "song.vgz");
            Directory.CreateDirectory(Path.GetDirectoryName(input)!);
            File.WriteAllText(input, "x");
            string output = Path.Combine(directory, "out.mp4");
            string projectPath = Path.Combine(directory, "song.mdpviz.json");

            VisualizationProject saved = VisualizationProjectStore.Save(
                new VisualizationProject
                {
                    Request = TestRequests.Valid(input, output),
                },
                projectPath);

            Assert.Equal(Path.Combine("nested", "song.vgz"), saved.RelativeInputPath);
            Assert.Equal("out.mp4", saved.RelativeOutputPath);

            VisualizationProject loaded = VisualizationProjectStore.Load(projectPath);
            Assert.Equal(input, loaded.Request.InputPath);
            Assert.Equal(output, loaded.Request.OutputPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AbsolutePathOutsideProject_IsPreserved()
    {
        string directory = CreateTempDir();
        try
        {
            string outside = Path.Combine(Path.GetTempPath(), "elsewhere", "song.vgz");
            string projectPath = Path.Combine(directory, "song.mdpviz.json");

            VisualizationProject saved = VisualizationProjectStore.Save(
                new VisualizationProject { Request = TestRequests.Valid(outside, outside + ".mp4") },
                projectPath);

            Assert.Null(saved.RelativeInputPath);
            VisualizationProject loaded = VisualizationProjectStore.Load(projectPath);
            Assert.Equal(outside, loaded.Request.InputPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Recovery_SaveLoadClear()
    {
        string directory = CreateTempDir();
        try
        {
            string recoveryDir = Path.Combine(directory, "recovery");
            Assert.Null(VisualizationProjectStore.TryLoadRecovery(recoveryDir));

            VisualizationProjectStore.SaveRecovery(
                new VisualizationProject { Request = TestRequests.Valid() }, recoveryDir);

            VisualizationProject? restored = VisualizationProjectStore.TryLoadRecovery(recoveryDir);
            Assert.NotNull(restored);
            Assert.Equal(
                VisualizationRequestSerializer.Serialize(TestRequests.Valid()),
                VisualizationRequestSerializer.Serialize(restored.Request));

            VisualizationProjectStore.ClearRecovery(recoveryDir);
            Assert.Null(VisualizationProjectStore.TryLoadRecovery(recoveryDir));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NewerSchema_Throws()
    {
        string directory = CreateTempDir();
        try
        {
            string projectPath = Path.Combine(directory, "future.mdpviz.json");
            File.WriteAllText(projectPath, """
                {
                  "schemaVersion": 99,
                  "request": { "schemaVersion": 99, "inputPath": "/tmp/x.vgz", "outputPath": "/tmp/o.mp4" }
                }
                """);
            Assert.Throws<VisualizationProjectException>(() => VisualizationProjectStore.Load(projectPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MalformedJson_Throws()
    {
        string directory = CreateTempDir();
        try
        {
            string projectPath = Path.Combine(directory, "bad.mdpviz.json");
            File.WriteAllText(projectPath, "{ nope");
            Assert.Throws<VisualizationProjectException>(() => VisualizationProjectStore.Load(projectPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"mdpviz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
