using System.Diagnostics;
using Fmp.Core.Visualization.Model;
using Fmp.Core.Visualization.Render.PanelLayout;

var visJson = "/home/jose/MDPlayer/XA2047.visualization/visualization.json";
var doc = VisualizationDocument.Load(visJson);
var provider = new WavFileScopeProvider(
    "/home/jose/MDPlayer/XA2047.visualization/master.wav",
    channelStemPaths: null,
    sampleRate: doc.Timeline.AudioSampleRate,
    allowMasterFallback: true);
var panels = new LogicalPanelBuilder().Build(doc);
var planner = new ScopeTriggerPlanner(provider,
    doc.Timeline.AudioSampleRate, doc.Timeline.VideoFpsNumerator,
    doc.Timeline.VideoFpsDenominator, doc.Timeline.TotalVideoFrames);

Console.WriteLine($"Building plan for {doc.Timeline.TotalVideoFrames} frames...");
var sw = Stopwatch.StartNew();
var plan = planner.Build(panels);
sw.Stop();
Console.WriteLine($"  Plan build: {sw.Elapsed.TotalSeconds:F2}s " +
    $"({sw.Elapsed.TotalMilliseconds / doc.Timeline.TotalVideoFrames:F2}ms/frame)");

var renderer = new PanelFrameRenderer(doc, provider, triggerPlan: plan);

// Warmup
renderer.RenderFrame(1000);

// Timed batch
const int frames = 30;
int startFrame = 2000;
Console.WriteLine($"\nRendering {frames} frames (sequential, start={startFrame})...");
sw.Restart();
for (int fi = startFrame; fi < startFrame + frames; fi++)
    renderer.RenderFrame(fi);
sw.Stop();
Console.WriteLine($"  Total: {sw.Elapsed.TotalSeconds:F2}s");
Console.WriteLine($"  Per frame: {sw.Elapsed.TotalMilliseconds / frames:F1}ms");
Console.WriteLine($"  FPS: {frames / sw.Elapsed.TotalSeconds:F0}");
