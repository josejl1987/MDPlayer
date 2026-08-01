using System.Diagnostics;
using MDPlayer.Fmp.Tests.Fixtures;
using Fmp.Core.Visualization.Render.PanelLayout;

var sw = Stopwatch.StartNew();
var doc = VisualizationDocumentFixture.CreateTwoSecond();
var renderer = new PanelFrameRenderer(doc);

// Profile sections manually
var swAlloc = new Stopwatch();
var swDraw = new Stopwatch();
var swConvert = new Stopwatch();

const int iterations = 30;
for (int iter = 0; iter < iterations; iter++)
{
    swAlloc.Start();
    var pixels = new SixLabors.ImageSharp.PixelFormats.Rgba32[1920 * 1080];
    swAlloc.Stop();
    
    for (int fi = 0; fi < 4; fi++) // 4 frames
    {
        swDraw.Start();
        var frame = renderer.RenderFrame(fi);
        swDraw.Stop();
        
        swConvert.Start();
        var result = new byte[1920 * 1080 * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            int off = i * 4;
            result[off] = pixels[i].R;
            result[off + 1] = pixels[i].G;
            result[off + 2] = pixels[i].B;
            result[off + 3] = 255;
        }
        swConvert.Stop();
    }
}

Console.WriteLine($"Alloc:   {swAlloc.Elapsed.TotalMilliseconds / iterations:F1}ms/iter");
Console.WriteLine($"Draw:    {swDraw.Elapsed.TotalMilliseconds / (iterations * 4):F1}ms/frame");
Console.WriteLine($"Convert: {swConvert.Elapsed.TotalMilliseconds / (iterations * 4):F1}ms/frame");
