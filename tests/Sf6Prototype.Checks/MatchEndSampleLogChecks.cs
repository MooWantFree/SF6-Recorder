using System.Diagnostics;
using System.Text.Json;
using Sf6Prototype;

public static class MatchEndSampleLogChecks
{
    public static int Run()
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);var now=new DateTimeOffset(2026,11,14,3,0,0,TimeSpan.FromHours(8));
        var path=Path.Combine(root,"match-detection-samples.jsonl");
        using(var log=new MatchEndSampleLog(root))
        {
            log.Append(now,1,new(true,0.42),false,true,false,"sampling",4,0.8,"none");
            log.Append(now.AddMilliseconds(250),2,new(false,0.83),true,true,false,"sampling",4,1.1,"none");
            log.Append(now.AddMilliseconds(500),3,new(false,0.92),true,false,true,"sampling",4,1.2,"waiting");
            Check(!File.Exists(path),"Individual decisions stay in memory until the batch is flushed");
            log.Flush();
            var rows=File.ReadAllLines(path).Select(line=>JsonDocument.Parse(line)).ToArray();
            try
            {
                Check(rows.Length==3 && log.WrittenEntries==3 && log.FlushCount==1,"One flush retains every buffered judgment as its own JSON line");
                var first=rows[0].RootElement;var end=rows[2].RootElement;
                Check(first.GetProperty("at").GetDateTimeOffset()==now && first.GetProperty("score").GetDouble()==0.42,"Judgments preserve the sample timestamp and recognition score");
                Check(rows.Select(r=>r.RootElement.GetProperty("result").GetString()).SequenceEqual(["battle_hud","confirming_end","match_end"]),"The log distinguishes battle evidence, a pending result and a confirmed end");
                Check(end.GetProperty("armedBefore").GetBoolean() && !end.GetProperty("armed").GetBoolean() && end.GetProperty("sample").GetInt64()==3,"End rows preserve the state transition and exact sample number");
                Check(first.GetProperty("session").GetString()==end.GetProperty("session").GetString() && end.GetProperty("sequence").GetInt64()==3,"Session and sequence identify ordering across samples");
            }
            finally {foreach(var row in rows)row.Dispose();}
            log.Append(now.AddSeconds(1),null,null,false,false,false,"not_foreground",4,null,"none");
        }
        Check(File.ReadAllLines(path).Length==4,"Stopping flushes the partial batch rather than discarding the final decisions");
        using(var log=new MatchEndSampleLog(root))
        {
            log.Append(now.AddMinutes(1),1,new(false,0.9),false,false,false,"sampling",2,1,"none");
        }
        using(var last=JsonDocument.Parse(File.ReadAllLines(path).Last()))
            Check(last.RootElement.GetProperty("result").GetString()=="not_armed" && File.ReadAllLines(path).Length==5,"Restarting appends a new session and explains suppressed stale results");
        var pixels=new byte[MatchEndDetection.FrameWidth*MatchEndDetection.FrameHeight*4];
        var failures=new MatchEndFailureTracker();
        for(var i=0;i<20;i++)
            Check(failures.Observe(now.AddMilliseconds(i*250),i,new(true,0.54),true,false,4,pixels)==null,"Normal battle frames never request a failure image");
        Check(failures.Interrupt("not_foreground")==null,"Switching away without a suspicious result does not save an image");
        Check(failures.Observe(now,10,new(false,0.70),true,false,4,pixels)==null,"A suspicious first frame is buffered, not written immediately");
        pixels[0]=76;
        failures.Observe(now.AddMilliseconds(250),11,new(false,0.76),true,false,4,pixels);
        pixels[0]=1;
        failures.Observe(now.AddMilliseconds(500),12,new(false,0.71),true,false,4,pixels);
        var missed=failures.Observe(now.AddMilliseconds(750),13,new(true,0.5),true,false,4,pixels);
        Check(missed is {Sample:11,Armed:true,Reason:"candidate_lost"} && missed.Pixels[0]==76 && missed.Observation.ResultScore==0.76,"A disappearing candidate retains the exact highest-scoring input frame and sample identity");
        Check(failures.Interrupt("not_foreground")==null,"A failed candidate generates only one image request");
        failures.Observe(now,20,new(false,0.9),true,false,4,pixels);
        Check(failures.Observe(now.AddMilliseconds(250),21,new(false,0.91),false,true,4,pixels)==null && failures.Interrupt("stopped")==null,"Successful confirmation cancels the pending failure image");
        Check(Enumerable.Range(0,20).All(i=>failures.Observe(now.AddSeconds(1+i*0.25),22+i,new(false,0.8403),false,false,4,pixels)==null)
            && failures.Interrupt("not_foreground")==null,"The real 03:06 result score cannot generate false failure evidence after the match was already confirmed");
        failures.Observe(now.AddSeconds(7),43,new(false,0.8403),false,false,4,pixels);
        Check(failures.Interrupt("stopped")==null,"Returning from alt-tab to an already confirmed result remains suppressed");
        failures.Observe(now.AddSeconds(8),44,new(true,0.4),true,false,4,pixels);
        failures.Observe(now.AddSeconds(9),45,new(false,0.72),true,false,4,pixels);
        Check(failures.Interrupt("candidate_lost") is {Sample:45},"A later armed battle can still preserve a genuine unconfirmed result");
        failures=new();
        failures.Observe(now,30,new(false,0.72),false,false,4,pixels);
        var timeout=failures.Observe(now.AddSeconds(2),31,new(false,0.73),false,false,4,pixels);
        Check(timeout is {Armed:false,Reason:"confirmation_timeout"},"A persistent unconfirmed result is saved even if battle arming was missed");
        Check(failures.Observe(now.AddSeconds(3),32,new(false,0.74),false,false,4,pixels)==null && failures.Interrupt("stopped")==null,"Persistent suspicion never encodes an image on every sample");
        failures.Observe(now,40,new(false,0.9),true,false,0.5,pixels);
        Check(failures.Observe(now.AddSeconds(2),41,new(false,0.91),true,false,0.5,pixels)==null,"A low sampling rate receives enough time to confirm before requesting an image");
        var slow=failures.Observe(now.AddSeconds(5),42,new(false,0.92),true,false,0.5,pixels);
        Check(slow!=null,"An unconfirmed low-frequency candidate eventually generates evidence");
        failures=new();
        failures.Observe(now,50,new(false,0.3),true,false,4,pixels);
        failures.Observe(now.AddMilliseconds(250),51,new(false,0.3),true,false,4,pixels);
        Check(failures.Observe(now.AddMilliseconds(500),52,new(true,0.4),true,false,4,pixels)==null,"Brief HUD loss does not write diagnostic images");
        failures.Observe(now.AddSeconds(1),53,new(false,0.2),true,false,4,pixels);
        pixels[0]=83;
        failures.Observe(now.AddSeconds(2),54,new(false,0.2),true,false,4,pixels);
        var unknown=failures.Interrupt("not_foreground");
        Check(unknown is {Sample:54,Reason:"hud_missing_not_foreground"} && unknown.Pixels[0]==83,"A persistent unknown result leaves evidence even below the template threshold");
        Check(failures.Interrupt("stopped")==null,"Unknown headers also generate only one image per candidate");
        failures.Observe(now,55,new(true,0.72),true,false,4,pixels);
        Check(failures.Interrupt("stopped") is {Sample:55},"A suspicious localized header is retained even when its background resembles HUD colors");
        using(var log=new MatchEndSampleLog(root))log.AppendFailure(missed!,"match-detection-failures/example.png",null);
        using(var row=JsonDocument.Parse(File.ReadAllLines(path).Last()))
            Check(row.RootElement.GetProperty("result").GetString()=="unconfirmed_result" &&
                row.RootElement.GetProperty("sample").GetInt64()==11 && row.RootElement.GetProperty("image").GetString()=="match-detection-failures/example.png", "Failure events link the picture back to its original judgment");

        var rotateRoot=Path.Combine(root,"rotation");
        using(var log=new MatchEndSampleLog(rotateRoot,maxFileBytes:1024,backupFiles:3,batchRows:4))
            for(var i=0;i<30;i++) log.Append(now.AddMilliseconds(i*250),i,new(false,0.4),true,true,false,"sampling",4,1,"none");
        var files=Directory.GetFiles(rotateRoot,"*.jsonl");
        Check(files.Length==4 && files.All(file=>new FileInfo(file).Length<=1024),"Rotation limits disk usage and only retains the current file plus three archives");
        var retained=new List<long>();
        foreach(var file in files)
            foreach(var line in File.ReadLines(file))
            {using var row=JsonDocument.Parse(line);retained.Add(row.RootElement.GetProperty("sequence").GetInt64());}
        Check(retained.Contains(30) && retained.Count==retained.Distinct().Count(),"Rotated files contain complete JSON lines and preserve the newest decision without duplicates");
        var failureRoot=Path.Combine(root,"failure");Directory.CreateDirectory(Path.Combine(failureRoot,"match-detection-samples.jsonl"));
        using(var log=new MatchEndSampleLog(failureRoot,batchRows:2))
        {
            for(var i=0;i<8;i++) log.Append(now,i,new(false,0.5),true,true,false,"sampling",4,1,"none");
            Check(log.LastError!=null && log.DroppedEntries==8,"Unavailable log storage is reported without throwing into the detector or retaining an unbounded backlog");
        }
        return count;
    }
    public static void Benchmark()
    {
        var root=Path.Combine(Environment.CurrentDirectory,".local","match-detection","sample-log-benchmark",Guid.NewGuid().ToString("N"));
        var samples=new List<double>();var allocated=0L;var cpu=TimeSpan.Zero;
        // Warm serialization/JIT separately from the measured four-Hz workload.
        using(var warm=new MatchEndSampleLog(Path.Combine(root,"warm")))
            for(var i=0;i<64;i++)warm.Append(DateTimeOffset.UtcNow,i,new(false,0.5),true,true,false,"sampling",4,1,"none");
        using(var process=Process.GetCurrentProcess())
        using(var log=new MatchEndSampleLog(root))
        {
            var beforeCpu=process.TotalProcessorTime;var wall=Stopwatch.StartNew();
            for(var i=0;i<64;i++)
            {
                var before=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();
                log.Append(DateTimeOffset.UtcNow,i,new(false,0.85),true,true,false,"sampling",4,1.2,"none");log.FlushIfDue();
                samples.Add(watch.Elapsed.TotalMilliseconds);allocated+=GC.GetAllocatedBytesForCurrentThread()-before;
                Thread.Sleep(Math.Max(0,250-(int)watch.Elapsed.TotalMilliseconds));
            }
            log.Flush();cpu=process.TotalProcessorTime-beforeCpu;
            var ordered=samples.Order().ToArray();
            var report=new {frequency=4,samples=samples.Count,meanMilliseconds=samples.Average(),
                p95Milliseconds=ordered[(int)((ordered.Length-1)*0.95)],maxMilliseconds=samples.Max(),allocatedBytesPerSample=allocated/(double)samples.Count,
                processCpuPercent=cpu.TotalMilliseconds/wall.Elapsed.TotalMilliseconds/Environment.ProcessorCount*100,
                log.WrittenEntries,log.DroppedEntries,log.FlushCount,log.LastError,
                fileBytes=new FileInfo(log.FilePath).Length,at=DateTimeOffset.UtcNow};
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            var json=JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true});
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(root)!,"latest.json"),json);Console.WriteLine(json);
        }
    }
}
