using Fmp.Core.Audio;
using Fmp.Core.Rendering;
using Fmp.Core.Rendering.Corrscope;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public class CorrscopeConfigWriterTests
{
    private ScopeRenderer.ScopeResult MakeSampleResult()
    {
        var result = new ScopeRenderer.ScopeResult
        {
            InputPath = "track.ovi",
            OutputDir = "/out",
            SampleRate = 44100,
            MasterSamples = 441000,
            Success = true,
            CompletionReason = "completed"
        };

        result.Stems.Add(new ScopeRenderer.StemResult
        {
            Name = "master", Label = "Master", StableOrder = 0,
            WavPath = "/out/audio/master.wav",
            RenderedSamples = 441000, Channels = 2, Success = true
        });

        result.Stems.Add(new ScopeRenderer.StemResult
        {
            Name = "ym2608-fm1", Label = "FM1", StableOrder = 10,
            WavPath = "/out/audio/ym2608-fm1.wav",
            RenderedSamples = 441000, Channels = 1, Success = true
        });

        result.Stems.Add(new ScopeRenderer.StemResult
        {
            Name = "ym2608-fm2", Label = "FM2", StableOrder = 11,
            WavPath = "/out/audio/ym2608-fm2.wav",
            RenderedSamples = 441000, Channels = 1, Success = true
        });

        // Rhythm (needs per-channel trigger override)
        result.Stems.Add(new ScopeRenderer.StemResult
        {
            Name = "ym2608-rhythm", Label = "RHYTHM", StableOrder = 30,
            SemanticClass = ScopeSemanticClass.Percussive,
            WindowWidth = 2,
            WavPath = "/out/audio/ym2608-rhythm.wav",
            RenderedSamples = 441000, Channels = 1, Success = true
        });

        // A failed stem (should be skipped)
        result.Stems.Add(new ScopeRenderer.StemResult
        {
            Name = "ym2608-ssg1", Label = "SSG1", StableOrder = 20,
            WavPath = "/out/audio/ym2608-ssg1.wav",
            RenderedSamples = 0, Channels = 1, Success = false
        });

        return result;
    }

    [Fact]
    public void Write_ProducesValidYamlWithMasterAudio()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-corrscope-").FullName;
        try
        {
            string yamlPath = Path.Combine(dir, "corrscope.yaml");
            var result = MakeSampleResult();
            CorrscopeConfigWriter.Write(yamlPath, dir, result);

            Assert.True(File.Exists(yamlPath));
            string yaml = File.ReadAllText(yamlPath);

            Assert.Contains("!Config", yaml);
            Assert.Contains("master_audio: audio/master.wav", yaml);
            Assert.Contains("fps: 60", yaml);
            Assert.Contains("!CorrelationTriggerConfig", yaml);
            Assert.Contains("!ChannelConfig", yaml);
            Assert.Contains("!LayoutConfig", yaml);
            Assert.Contains("!RendererConfig", yaml);
            Assert.Contains("width: 1920", yaml);
            Assert.DoesNotContain("!FFmpegOutputConfig", yaml);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_HasCorrectDefaultValues()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-corrscope-").FullName;
        try
        {
            string yamlPath = Path.Combine(dir, "corrscope.yaml");
            var result = MakeSampleResult();
            CorrscopeConfigWriter.Write(yamlPath, dir, result);

            string yaml = File.ReadAllText(yamlPath);

            // Trigger defaults (author's 2025 preset)
            Assert.Contains("trigger_ms: 30", yaml);
            Assert.Contains("render_ms: 16", yaml);
            Assert.Contains("amplification: 1", yaml);
            Assert.Contains("edge_strength: 1.2", yaml);
            Assert.Contains("reset_below: 0.6", yaml);
            Assert.Contains("buffer_falloff: 0.5", yaml);
            Assert.Contains("responsiveness: 0.8", yaml);

            // Pitch tracking must be null (disabled) not !SpectrumConfig {}
            Assert.Contains("pitch_tracking:", yaml);
            Assert.DoesNotContain("!SpectrumConfig", yaml);

            // Visual defaults — dark theme, no bright grid, no midline framing
            Assert.Contains("bg_color: \"#080a0f\"", yaml);
            Assert.Contains("grid_color: \"#10141c\"", yaml);
            Assert.Contains("midline_color: \"#10141c\"", yaml);
            Assert.Contains("grid_line_width: 0.5", yaml);
            Assert.Contains("v_midline: false", yaml);
            Assert.Contains("h_midline: false", yaml);
            Assert.Contains("line_width: 2.5", yaml);
            Assert.Contains("antialiasing: true", yaml);

            // Label styling
            Assert.Contains("label_font:", yaml);
            Assert.Contains("!Font", yaml);
            Assert.Contains("size: 20", yaml);
            Assert.Contains("bold: true", yaml);
            Assert.Contains("!LabelPosition LeftTop", yaml);
            Assert.Contains("label_padding_ratio: 0.5", yaml);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_FmChannel_UsesDefaultWidths()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-corrscope-").FullName;
        try
        {
            string yamlPath = Path.Combine(dir, "corrscope.yaml");
            var result = MakeSampleResult();
            CorrscopeConfigWriter.Write(yamlPath, dir, result);

            string fmSection = GetChannelSection(File.ReadAllText(yamlPath), "ym2608-fm1");
            Assert.DoesNotContain("trigger_width:", fmSection);
            Assert.DoesNotContain("render_width:", fmSection);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_RhythmChannel_HasPerChannelTriggerOverride()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-corrscope-").FullName;
        try
        {
            string yamlPath = Path.Combine(dir, "corrscope.yaml");
            var result = MakeSampleResult();
            CorrscopeConfigWriter.Write(yamlPath, dir, result);

            string yaml = File.ReadAllText(yamlPath);
            // Rhythm should have a per-channel trigger with buffer_strength: 0
            Assert.Contains("ym2608-rhythm", yaml);
            Assert.Contains("buffer_strength: 0", yaml);
            Assert.Contains("edge_strength: 2.5", yaml);
            Assert.Contains("responsiveness: 1", yaml);
            Assert.Contains("reset_below: 0", yaml);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_UsesSemanticClassForTriggerSelection()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-corrscope-semantic-").FullName;
        try
        {
            var result = new ScopeRenderer.ScopeResult
            {
                OutputDir = dir,
                SampleRate = 44100,
                MasterSamples = 44100,
                Success = true,
            };
            result.Stems.Add(new ScopeRenderer.StemResult
            {
                Name = "master", Label = "Master", Success = true,
            });
            result.Stems.Add(new ScopeRenderer.StemResult
            {
                Name = "renamed-impact-track",
                Label = "Impact",
                SemanticClass = ScopeSemanticClass.Percussive,
                Success = true,
            });

            string yamlPath = Path.Combine(dir, "semantic.yaml");
            CorrscopeConfigWriter.Write(
                yamlPath,
                dir,
                result,
                overrides: new CorrscopeOverrides { IncludeSilentChannels = true });

            string section = GetChannelSection(File.ReadAllText(yamlPath), "renamed-impact-track");
            Assert.Contains("edge_strength: 2.5", section);
            Assert.Contains("buffer_strength: 0", section);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_RhythmChannel_HasRenderWidthMultiplier()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-corrscope-").FullName;
        try
        {
            string yamlPath = Path.Combine(dir, "corrscope.yaml");
            CorrscopeConfigWriter.Write(yamlPath, dir, MakeSampleResult());

            string rhythmSection = GetChannelSection(File.ReadAllText(yamlPath), "ym2608-rhythm");
            Assert.Contains("render_width: 2", rhythmSection);
            Assert.Contains("trigger_width: 2", rhythmSection);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_FailedStemsAreSkipped()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-corrscope-").FullName;
        try
        {
            string yamlPath = Path.Combine(dir, "corrscope.yaml");
            var result = MakeSampleResult();
            result.Stems[0].Success = false; // master fails
            CorrscopeConfigWriter.Write(yamlPath, dir, result);

            string yaml = File.ReadAllText(yamlPath);
            Assert.Contains("ym2608-fm1", yaml);   // successful non-master stem
            Assert.DoesNotContain("ym2608-ssg1", yaml); // failed stem
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_Overrides_AreApplied()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-corrscope-").FullName;
        try
        {
            string yamlPath = Path.Combine(dir, "corrscope.yaml");
            var result = MakeSampleResult();

            var overrides = new CorrscopeOverrides
            {
                Fps = 30, Amplification = 0.5, TriggerMs = 60,
                RenderWidth = 1280, RenderHeight = 720,
                EdgeStrength = 2.0, ResetBelow = 0.5
            };

            CorrscopeConfigWriter.Write(yamlPath, dir, result, overrides: overrides);

            string yaml = File.ReadAllText(yamlPath);
            Assert.Contains("fps: 30", yaml);
            Assert.Contains("amplification: 0.5", yaml);
            Assert.Contains("trigger_ms: 60", yaml);
            Assert.Contains("edge_strength: 2", yaml);
            Assert.Contains("reset_below: 0.5", yaml);
            Assert.Contains("width: 1280", yaml);
            Assert.Contains("height: 720", yaml);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_DevModeOverrides_DisableAntialiasingAndHalfResolution()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-corrscope-").FullName;
        try
        {
            string yamlPath = Path.Combine(dir, "corrscope.yaml");
            var result = MakeSampleResult();

            var overrides = new CorrscopeOverrides
            {
                Fps = 30,
                RenderWidth = 1280,
                RenderHeight = 672,
                ResDivisor = 2.0,
                Antialiasing = false,
            };

            CorrscopeConfigWriter.Write(yamlPath, dir, result, overrides: overrides);

            string yaml = File.ReadAllText(yamlPath);
            Assert.Contains("res_divisor: 2", yaml);
            Assert.Contains("antialiasing: false", yaml);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_PerChannelAmplification_ComputedFromWav()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-corrscope-").FullName;
        try
        {
            string audioDir = Path.Combine(dir, "audio");
            Directory.CreateDirectory(audioDir);
            WriteMonoWav(Path.Combine(audioDir, "ym2608-fm1.wav"), 44100, 1000);

            string yamlPath = Path.Combine(dir, "corrscope.yaml");
            CorrscopeConfigWriter.Write(yamlPath, dir, MakeSampleResult());

            string fmSection = GetChannelSection(File.ReadAllText(yamlPath), "ym2608-fm1");
            string amplificationLine = fmSection.Split('\n')
                .Single(line => line.TrimStart().StartsWith("amplification:", StringComparison.Ordinal));
            string amplificationText = amplificationLine[(amplificationLine.IndexOf(':') + 1)..].Trim();
            Assert.True(double.TryParse(amplificationText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double gain));
            Assert.True(gain > 1.0, $"Expected computed gain > 1, got {gain}.");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string GetChannelSection(string yaml, string channelName)
    {
        string marker = $"wav_path: \"audio/{channelName}.wav\"";
        int start = yaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Channel {channelName} was not emitted.");
        int end = yaml.IndexOf("\n- !ChannelConfig", start + marker.Length, StringComparison.Ordinal);
        return yaml[start..(end >= 0 ? end : yaml.Length)];
    }

    private static void WriteMonoWav(string path, int sampleRate, int sampleCount)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        const short blockAlign = channels * (bitsPerSample / 8);
        int dataSize = sampleCount * blockAlign;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: false);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * i / 32) * 16384);
            writer.Write(sample);
        }
    }

    [Fact]
    public void Write_FixedGridIncludesSilentChannelsAndHidesLabels()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-corrscope-grid-").FullName;
        try
        {
            var result = new ScopeRenderer.ScopeResult
            {
                OutputDir = dir,
                SampleRate = 44100,
                MasterSamples = 44100,
                Success = true,
            };
            foreach (StemPass pass in DefaultStems.All)
            {
                result.Stems.Add(new ScopeRenderer.StemResult
                {
                    Name = pass.Name,
                    Label = pass.Label,
                    SemanticClass = pass.SemanticClass,
                    StableOrder = pass.StableOrder,
                    WindowWidth = pass.WindowWidth,
                    DefaultAmplification = pass.DefaultAmplification,
                    DefaultColor = pass.DefaultColor,
                    WavPath = Path.Combine(dir, "audio", pass.Name + ".wav"),
                    RenderedSamples = 44100,
                    Channels = pass.Name == "master" ? 2 : 1,
                    Success = true,
                });
            }

            string yamlPath = Path.Combine(dir, "corrscope-grid.yaml");
            CorrscopeConfigWriter.Write(
                yamlPath,
                dir,
                result,
                overrides: new CorrscopeOverrides
                {
                    LayoutNCols = 3,
                    IncludeSilentChannels = true,
                    HideLabels = true,
                });

            string yaml = File.ReadAllText(yamlPath);
            Assert.Equal(12, CountOccurrences(yaml, "- !ChannelConfig"));
            Assert.Equal(12, CountOccurrences(yaml, "  label: \"\""));
            Assert.Contains("  ncols: 3", yaml);

            int fm1 = yaml.IndexOf("audio/ym2608-fm1.wav", StringComparison.Ordinal);
            int ssg1 = yaml.IndexOf("audio/ym2608-ssg1.wav", StringComparison.Ordinal);
            int rhythm = yaml.IndexOf("audio/ym2608-rhythm.wav", StringComparison.Ordinal);
            int adpcm = yaml.IndexOf("audio/ym2608-adpcm.wav", StringComparison.Ordinal);
            int ppz8 = yaml.IndexOf("audio/ppz8-01.wav", StringComparison.Ordinal);
            Assert.True(fm1 < ssg1 && ssg1 < rhythm && rhythm < adpcm && adpcm < ppz8);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Write_FocusGridContainsOnlySelectedStems()
    {
        string dir = Directory.CreateTempSubdirectory("fmp-corrscope-focus-").FullName;
        try
        {
            var result = new ScopeRenderer.ScopeResult
            {
                OutputDir = dir,
                SampleRate = 44100,
                MasterSamples = 44100,
                Success = true,
            };
            foreach (StemPass pass in DefaultStems.All.Where(pass =>
                pass.Name is "master" or "ym2608-fm1" or "ym2608-fm3"))
            {
                result.Stems.Add(new ScopeRenderer.StemResult
                {
                    Name = pass.Name,
                    Label = pass.Label,
                    SemanticClass = pass.SemanticClass,
                    StableOrder = pass.StableOrder,
                    WindowWidth = pass.WindowWidth,
                    DefaultAmplification = pass.DefaultAmplification,
                    DefaultColor = pass.DefaultColor,
                    WavPath = Path.Combine(dir, "audio", pass.Name + ".wav"),
                    RenderedSamples = 44100,
                    Channels = pass.Name == "master" ? 2 : 1,
                    Success = true,
                });
            }

            string yamlPath = Path.Combine(dir, "corrscope-focus.yaml");
            CorrscopeConfigWriter.Write(
                yamlPath,
                dir,
                result,
                overrides: new CorrscopeOverrides
                {
                    LayoutNCols = 2,
                    IncludeSilentChannels = true,
                    HideLabels = true,
                });

            string yaml = File.ReadAllText(yamlPath);
            Assert.Equal(2, CountOccurrences(yaml, "- !ChannelConfig"));
            Assert.Contains("audio/ym2608-fm1.wav", yaml);
            Assert.Contains("audio/ym2608-fm3.wav", yaml);
            Assert.DoesNotContain("audio/ym2608-fm2.wav", yaml);
            Assert.Contains("  ncols: 2", yaml);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

}
