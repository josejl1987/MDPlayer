using System.Text.Json;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

/// <summary>
/// Writes a self-contained canvas preview with a time scrubber. It intentionally
/// consumes the same timeline and prepared layout plan as video composition.
/// </summary>
internal static class VisualizationPreviewWriter
{
    public static void Write(
        string path,
        VisualizationTimeline timeline,
        VisualizationLayoutPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(plan);

        string timelineJson = VisualizationJsonWriter.Serialize(timeline)
            .Replace("<", "\\u003c", StringComparison.Ordinal)
            .Replace(">", "\\u003e", StringComparison.Ordinal)
            .Replace("&", "\\u0026", StringComparison.Ordinal);
        string planJson = plan.ToJson()
            .Replace("<", "\\u003c", StringComparison.Ordinal)
            .Replace(">", "\\u003e", StringComparison.Ordinal)
            .Replace("&", "\\u0026", StringComparison.Ordinal);

        string html = $$"""
<!doctype html>
<meta charset="utf-8">
<title>MDPlayer Visualization Preview</title>
<style>
body{margin:0;background:#0b0c13;color:#dee2ee;font:14px system-ui,sans-serif}
main{max-width:1400px;margin:auto;padding:16px} canvas{display:block;width:100%;background:#0f1119;border:1px solid #4c5267}
label{display:inline-flex;gap:8px;align-items:center;margin-top:10px} input{accent-color:#9a7dff;width:70vw}
</style>
<main><canvas id="view"></canvas><label>Time <input id="time" type="range" min="0" max="1" step="0.001" value="0"><output id="clock">0.00s</output></label></main>
<script>
const timeline={{timelineJson}};
const plan={{planJson}};
const canvas=document.getElementById('view'),ctx=canvas.getContext('2d');
const time=document.getElementById('time'),clock=document.getElementById('clock');
const notes=timeline.notes||[], voices=new Map((timeline.voices||[]).map(v=>[v.id,v]));
const end=Math.max(1,timeline.endSample-timeline.startSample), rate=timeline.sampleRate||1;
canvas.width=plan.width;canvas.height=plan.height;
function color(id){let h=2166136261;for(const c of id||''){h=((h^c.charCodeAt(0))*16777619)>>>0}return `hsl(${h%360} 65% 58%)`}
function draw(){
 const sample=timeline.startSample+Number(time.value)*end, past=plan.pastSeconds*rate, future=plan.futureSeconds*rate;
 ctx.fillStyle='#0b0c13';ctx.fillRect(0,0,canvas.width,canvas.height);
 ctx.fillStyle='#13151f';ctx.fillRect(0,0,canvas.width,plan.headerHeight);
 ctx.fillStyle='#dee2ee';ctx.font='bold 18px system-ui';ctx.fillText(timeline.source?.title||'MDPlayer Preview',16,24);
 const region=plan.regions.find(r=>r.kind==='semantic')||plan.regions.find(r=>r.kind==='panel');
 if(!region)return;
 const selected=plan.tracks.filter(t=>t.selected), rowH=Math.max(16,(region.height-24)/Math.max(1,selected.length));
 ctx.strokeStyle='rgba(120,132,164,.25)';ctx.lineWidth=1;
 for(let i=0;i<=10;i++){let x=region.x+region.width*i/10;ctx.beginPath();ctx.moveTo(x,region.y);ctx.lineTo(x,region.y+region.height);ctx.stroke()}
 selected.forEach((track,row)=>{
   const y=region.y+row*rowH, ids=new Set(track.sourceVoiceIds||[]);
   ctx.fillStyle='rgba(222,226,238,.75)';ctx.font='12px system-ui';ctx.fillText(track.label||track.id,region.x+4,y+13);
   for(const note of notes){if(!ids.has(note.voiceId)||note.endSample<=sample-past||note.startSample>=sample+future)continue;
     const x1=region.x+(note.startSample-(sample-past))/(past+future)*region.width;
     const x2=region.x+(note.endSample-(sample-past))/(past+future)*region.width;
     const left=Math.max(region.x,x1),right=Math.min(region.x+region.width,Math.max(x1+2,x2));
     if(right<=left)continue;ctx.fillStyle=color(note.instrumentId||note.voiceId);ctx.globalAlpha=note.startSample>sample?.55:.9;
     ctx.fillRect(left,y+18,Math.max(2,right-left),Math.max(4,rowH-24));
   }
 });
 ctx.globalAlpha=1;const play=region.x+region.width*plan.pastSeconds/(plan.pastSeconds+plan.futureSeconds);
 ctx.strokeStyle='#eef1fa';ctx.beginPath();ctx.moveTo(play,region.y);ctx.lineTo(play,region.y+region.height);ctx.stroke();
 clock.value=(Number(time.value)*end/rate).toFixed(2)+'s';
}
time.addEventListener('input',draw);draw();
</script>
""";

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        File.WriteAllText(fullPath, html);
    }
}
