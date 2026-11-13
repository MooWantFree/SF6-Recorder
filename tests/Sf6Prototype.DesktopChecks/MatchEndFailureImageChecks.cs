using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Sf6Prototype;

internal static class MatchEndFailureImageChecks
{
    public static int Run()
    {
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var folder=Path.Combine(root,"match-detection-failures");Directory.CreateDirectory(folder);
        var pixels=new byte[MatchEndDetection.FrameWidth*MatchEndDetection.FrameHeight*4];
        new Random(7).NextBytes(pixels);
        var monitor=new MatchEndMonitor(root);
        var save=typeof(MatchEndMonitor).GetMethod("SaveFrame",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var relative=Path.Combine("match-detection-failures","failure-check.png");
        var watch=Stopwatch.StartNew();
        if(save.Invoke(monitor,[pixels,relative]) is not true) throw new Exception("Failure evidence image was not saved");
        var imageMs=watch.Elapsed.TotalMilliseconds;
        using(var image=new Bitmap(Path.Combine(root,relative)))
        {
            var color=image.GetPixel(0,0);
            if(image.Width!=640 || image.Height!=64 || color.R!=pixels[2] || color.G!=pixels[1] || color.B!=pixels[0])
                throw new Exception("Saved failure evidence differs from the sampled input");
        }
        var failure=new MatchEndFailure(DateTimeOffset.Now,3,new(false,0.72),true,4,"candidate_lost",pixels);
        using(var log=new MatchEndSampleLog(root))log.AppendFailure(failure,relative.Replace('\\','/'),null);
        using(var row=JsonDocument.Parse(File.ReadAllLines(Path.Combine(root,"match-detection-samples.jsonl")).Single()))
            if(!File.Exists(Path.Combine(root,row.RootElement.GetProperty("image").GetString()!)))
                throw new Exception("A failure log does not point to a readable evidence image");
        var report=Path.Combine(Environment.CurrentDirectory,".local","match-detection","failure-image-benchmark.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report,JsonSerializer.Serialize(new {imageMilliseconds=imageMs,bytes=new FileInfo(Path.Combine(root,relative)).Length,
            source="640x64 synthetic input, one-off failure evidence; not per-frame work"}));
        return 3;
    }
}
