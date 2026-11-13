using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Drawing;
using System.Drawing.Imaging;

namespace Sf6Prototype;

public sealed class MatchEndMonitor(string directory)
{
    private CancellationTokenSource? _cancellation;
    private Task _worker=Task.CompletedTask;
    private string _refreshStatus="none";
    private double _samplesPerSecond=4;
    public void SetFrequency(double frequency)
    {
        if(!ConnectionSettings.ValidMatchEndFrequency(frequency)) throw new ArgumentOutOfRangeException(nameof(frequency));
        Volatile.Write(ref _samplesPerSecond,frequency);
    }
    public event Action<DateTimeOffset>? MatchEnded;
    public void ReportRefresh(string status)=>Volatile.Write(ref _refreshStatus,status);
    public void Start()
    {
        if(_cancellation!=null) return;
        _cancellation=new();var token=_cancellation.Token;
        _worker=Task.Run(()=>RunAsync(token));
    }
    public async Task StopAsync()
    {
        var cancellation=_cancellation;if(cancellation==null) return;
        cancellation.Cancel();await _worker;cancellation.Dispose();_cancellation=null;
    }
    private async Task RunAsync(CancellationToken token)
    {
        using var capture=new GameFrameCapture();using var process=Process.GetCurrentProcess();
        using var sampleLog=new MatchEndSampleLog(directory);
        var failures=new MatchEndFailureTracker();var failureImages=0;string? failureImageError=null;
        var detector=new MatchEndDetection();var started=Stopwatch.StartNew();var previousCpu=process.TotalProcessorTime;
        var previousAt=started.Elapsed;var nextProbe=TimeSpan.Zero;var nextReport=TimeSpan.Zero;
        Sf6Process? game=null;var previousPid=0;var samples=0L;var events=0;var sumMs=0d;var maxMs=0d;var cpuTicks=0L;
        var recent=new Queue<double>(256);var phase="game_not_running";var lastScore=0d;DateTimeOffset? lastEnded=null;string? error=null;
        string? loggedPhase=null;
        void SaveFailure(MatchEndFailure? failure)
        {
            if(failure==null) return;
            string? relative=null;
            try
            {
                var folder=Path.Combine(directory,"match-detection-failures");Directory.CreateDirectory(folder);
                var name=$"failure-{failure.At.ToLocalTime():yyyyMMdd-HHmmss-fff}-{failure.Sample}-{Guid.NewGuid():N}.png";
                if(!SaveFrame(failure.Pixels,Path.Combine("match-detection-failures",name)))
                    failureImageError="image_write_failed";
                else
                {
                    relative="match-detection-failures/"+name;failureImages++;failureImageError=null;
                    var root=Path.GetFullPath(folder)+Path.DirectorySeparatorChar;
                    foreach(var old in new DirectoryInfo(folder).EnumerateFiles("failure-*.png").OrderByDescending(file=>file.LastWriteTimeUtc).Skip(100))
                        if(old.FullName.StartsWith(root,StringComparison.OrdinalIgnoreCase)) old.Delete();
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {failureImageError=ex.GetType().Name;}
            sampleLog.AppendFailure(failure,relative,failureImageError);sampleLog.Flush();
        }
        void Report(bool enabled)
        {
            try
            {
                process.Refresh();var elapsed=started.Elapsed;var cpu=process.TotalProcessorTime;
                var delta=(elapsed-previousAt).TotalMilliseconds;
                var cpuPercent=delta>0 ? (cpu-previousCpu).TotalMilliseconds/delta/Environment.ProcessorCount*100 : 0;
                previousAt=elapsed;previousCpu=cpu;
                var ordered=recent.Order().ToArray();
                var value=new {recognizer=MatchEndDetection.Version,enabled,gameRunning=game!=null,phase,samplesPerSecond=Volatile.Read(ref _samplesPerSecond),samples,events,armed=detector.Armed,lastScore,lastEnded,
                    sampleMeanMilliseconds=samples>0 ? sumMs/samples : 0,sampleMaxMilliseconds=maxMs,
                    sampleP95Milliseconds=ordered.Length>0 ? ordered[(int)((ordered.Length-1)*0.95)] : 0,
                    detectorWorkerCpuPercent=elapsed.TotalMilliseconds>0 ? cpuTicks/10000d/elapsed.TotalMilliseconds/Environment.ProcessorCount*100 : 0,
                    processCpuPercent=cpuPercent,workingSetMiB=process.WorkingSet64/1048576d,
                    refresh=Volatile.Read(ref _refreshStatus),error,
                    sampleLog=new {writtenEntries=sampleLog.WrittenEntries,droppedEntries=sampleLog.DroppedEntries,flushes=sampleLog.FlushCount,error=sampleLog.LastError},
                    failureImages,failureImageError,
                    updatedAt=DateTimeOffset.UtcNow};
                var path=Path.Combine(directory,"match-detection.json");
                File.WriteAllText(path+".tmp",JsonSerializer.Serialize(value,new JsonSerializerOptions {WriteIndented=true}));
                File.Move(path+".tmp",path,true);
            }
            catch(IOException) { }
            catch(UnauthorizedAccessException) { }
        }
        try
        {
            while(!token.IsCancellationRequested)
            {
                var cycle=Stopwatch.GetTimestamp();
                var now=DateTimeOffset.UtcNow;
                if(started.Elapsed>=nextProbe)
                {
                    game=Sf6GameProbe.Find();nextProbe=started.Elapsed+TimeSpan.FromSeconds(2);
                    if((game?.Id ?? 0)!=previousPid)
                    {SaveFailure(failures.Interrupt("process_changed"));detector.Reset();previousPid=game?.Id ?? 0;}
                }
                var sampled=false;
                phase=game==null ? "game_not_running" : "not_foreground";
                if(game is { } active && GameFrameCapture.IsForeground(active))
                {
                    var cpuBefore=GameFrameCapture.CurrentThreadCpuTicks();var watch=Stopwatch.StartNew();
                    if(capture.TryCapture(active))
                    {
                        var observation=detector.Analyze(capture.Pixels);lastScore=observation.ResultScore;
                        sampled=true;phase="sampling";error=null;
                        var armedBefore=detector.Armed;var ended=detector.Observe(observation,now);
                        sampleLog.Append(now,samples+1,observation,armedBefore,detector.Armed,ended,phase,
                            Volatile.Read(ref _samplesPerSecond),watch.Elapsed.TotalMilliseconds,Volatile.Read(ref _refreshStatus));
                        SaveFailure(failures.Observe(now,samples+1,observation,detector.Armed,ended,
                            Volatile.Read(ref _samplesPerSecond),capture.Pixels));
                        sampleLog.FlushIfDue();
                        if(ended)
                        {
                            sampleLog.Flush();
                            events++;lastEnded=now;
                            SaveFrame(capture.Pixels,"match-detection-last-end.png");
                            MatchEnded?.Invoke(now);nextReport=TimeSpan.Zero;
                        }
                    }
                    else {phase="frame_unavailable";detector.Suspend();}
                    var ms=watch.Elapsed.TotalMilliseconds;cpuTicks+=Math.Max(0,GameFrameCapture.CurrentThreadCpuTicks()-cpuBefore);
                    if(sampled) {samples++;sumMs+=ms;maxMs=Math.Max(maxMs,ms);recent.Enqueue(ms);if(recent.Count>256)recent.Dequeue();}
                }
                else detector.Suspend();
                if(!sampled) SaveFailure(failures.Interrupt(phase));
                if(!sampled && phase!=loggedPhase)
                    sampleLog.Append(now,null,null,detector.Armed,detector.Armed,false,phase,
                        Volatile.Read(ref _samplesPerSecond),null,Volatile.Read(ref _refreshStatus));
                loggedPhase=phase;
                sampleLog.FlushIfDue();
                if(started.Elapsed>=nextReport)
                {
                    if(sampled) SaveFrame(capture.Pixels,"match-detection-last-frame.png");
                    Report(true);nextReport=started.Elapsed+TimeSpan.FromSeconds(15);
                }
                var interval=game==null ? 1000 : sampled ? 1000/Volatile.Read(ref _samplesPerSecond) : 500;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1,interval-Stopwatch.GetElapsedTime(cycle).TotalMilliseconds)),token);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {error=ex.GetType().Name;phase="unavailable";}
        finally
        {
            SaveFailure(failures.Interrupt("stopped"));
            sampleLog.Append(DateTimeOffset.UtcNow,null,null,detector.Armed,detector.Armed,false,"stopped",
                Volatile.Read(ref _samplesPerSecond),null,Volatile.Read(ref _refreshStatus));
            sampleLog.Flush();Report(false);
        }
    }
    private bool SaveFrame(byte[] pixels,string filename)
    {
        try
        {
            using var bitmap=new Bitmap(MatchEndDetection.FrameWidth,MatchEndDetection.FrameHeight,PixelFormat.Format32bppRgb);
            var data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.WriteOnly,bitmap.PixelFormat);
            try {Marshal.Copy(pixels,0,data.Scan0,pixels.Length);} finally {bitmap.UnlockBits(data);}
            bitmap.Save(Path.Combine(directory,filename),ImageFormat.Png);
            return true;
        }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        catch(ExternalException) { }
        return false;
    }
}
