using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sf6Prototype;

internal static class MatchEndVisionChecks
{
    internal static void Inspect(IEnumerable<string> paths)
    {
        var detector=new MatchEndDetection();
        foreach(var path in paths)
        {
            using var image=System.Drawing.Image.FromFile(path);
            var pixels=image.Width==640 && image.Height==64 ? ReadHeader(path) : Load(path);
            Console.WriteLine(JsonSerializer.Serialize(new {path,observation=detector.Analyze(pixels)}));
        }
    }
    internal static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        var root=Path.Combine(AppContext.BaseDirectory,"Fixtures","MatchEnd");
        var detector=new MatchEndDetection();var battle=ReadHeader(Path.Combine(root,"battle.png"));
        Check(detector.Analyze(battle).BattleHud && detector.Analyze(battle).ResultScore<MatchEndDetection.ResultThreshold,
            "A real battle HUD cannot match either localized result header");
        var reports=new List<object>();
        foreach(var (name,pattern) in new[]{("result-en","result_latin"),("result-ja","result_latin"),("result-zh-hans","result_zh_hans")})
        {
            var pixels=ReadHeader(Path.Combine(root,name+".png"));
            var observation=detector.Analyze(pixels);
            Check(observation.ResultScore>=MatchEndDetection.ResultThreshold && observation.ResultPattern==pattern,
                $"The actual {name} screenshot is recognized with its own result header: {observation}");
            detector.Reset();var now=DateTimeOffset.UtcNow;
            Check(!detector.Observe(observation,now),$"An old {name} result does not trigger on startup");
            for(var i=0;i<3;i++)detector.Observe(detector.Analyze(battle),now.AddMilliseconds(250+i*250));
            var events=0;
            for(var i=0;i<8;i++)if(detector.Observe(observation,now.AddSeconds(1+i*0.25)))events++;
            Check(events==1,$"The {name} image sequence triggers exactly once");
            if(name=="result-zh-hans")
            {
                detector.Reset();
                for(var i=0;i<3;i++)detector.Observe(detector.Analyze(battle),now.AddMilliseconds(i*250));
                Check(detector.Observe(observation,now.AddSeconds(1)),"The missed Chinese result is now strong enough for a one-sample quick-rematch confirmation");
            }
            for(var i=0;i<20;i++)detector.Analyze(pixels);
            var allocated=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();
            for(var i=0;i<100;i++)detector.Analyze(pixels);
            reports.Add(new {name,observation,meanMilliseconds=watch.Elapsed.TotalMilliseconds/100,
                allocatedBytesPerSample=(GC.GetAllocatedBytesForCurrentThread()-allocated)/100d});
        }
        var uniform=new byte[MatchEndDetection.FrameWidth*MatchEndDetection.FrameHeight*4];
        for(var i=0;i<uniform.Length;i+=4) {uniform[i]=230;uniform[i+1]=100;uniform[i+2]=220;}
        Check(detector.Analyze(uniform).ResultScore<MatchEndDetection.ResultThreshold,"A saturated purple background is not a result header");
        var report=Path.Combine(Environment.CurrentDirectory,".local","match-detection","localized-regression.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report,JsonSerializer.Serialize(new {recognizer=MatchEndDetection.Version,fixtures=reports,at=DateTimeOffset.UtcNow},new JsonSerializerOptions {WriteIndented=true}));
        return count;
    }
    private static byte[] ReadHeader(string path)
    {
        using var bitmap=new Bitmap(path);
        var rect=new Rectangle(0,0,MatchEndDetection.FrameWidth,MatchEndDetection.FrameHeight);
        var data=bitmap.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        var pixels=new byte[rect.Width*rect.Height*4];
        try {for(var row=0;row<rect.Height;row++)Marshal.Copy(data.Scan0+row*data.Stride,pixels,row*rect.Width*4,rect.Width*4);}
        finally {bitmap.UnlockBits(data);}
        return pixels;
    }
    internal static void Benchmark(string root)
    {
        var detector=new MatchEndDetection();var fixtures=new List<object>();
        var battle=Load(Path.Combine(root,"battle.jpg"));
        var result=Load(Path.Combine(root,"result-en.jpg"));
        foreach(var (name,pixels,expected) in new[]{("battle",battle,false),("result-en",result,true),
            ("result-jp",Load(Path.Combine(root,"result-jp-2.jpg"),pad:true),true)})
        {
            var observation=detector.Analyze(pixels);
            if((observation.ResultScore>=0.79)!=expected || name=="battle" && !observation.BattleHud)
                throw new Exception($"Unexpected visual result: {name} {observation}");
            for(var i=0;i<20;i++) detector.Analyze(pixels);
            var allocated=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();
            for(var i=0;i<300;i++) detector.Analyze(pixels);
            fixtures.Add(new {name,observation,meanMilliseconds=watch.Elapsed.TotalMilliseconds/300,
                allocatedBytesPerSample=(GC.GetAllocatedBytesForCurrentThread()-allocated)/300d});
        }
        var now=DateTimeOffset.UtcNow;
        for(var i=0;i<3;i++) detector.Observe(detector.Analyze(battle),now.AddMilliseconds(i*250));
        if(detector.Observe(detector.Analyze(result),now.AddSeconds(3)) ||
            !detector.Observe(detector.Analyze(result),now.AddSeconds(3.25)) ||
            detector.Observe(detector.Analyze(result),now.AddSeconds(3.5)))
            throw new Exception("Real image sequence did not generate exactly one end event");
        object? captureReport=null;
        // Never put a benchmark window over a game that the user has already started.
        if(Sf6GameProbe.Find()==null)
        {
            var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            var source=new BitmapImage(new Uri(Path.GetFullPath(Path.Combine(root,"result-en.jpg"))));
            var window=new Window {Title="SF6 detection performance check",Width=1280,Height=720,ShowActivated=false,Topmost=true,
                ShowInTaskbar=false,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
                Content=new System.Windows.Controls.Image {Source=source,Stretch=System.Windows.Media.Stretch.Fill}};
            window.Show();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);Thread.Sleep(150);
            var game=new Sf6Process(Environment.ProcessId,new WindowInteropHelper(window).Handle);
            using var capture=new GameFrameCapture();using var process=Process.GetCurrentProcess();
            if(!capture.TryCapture(game,requireForeground:false) || detector.Analyze(capture.Pixels).ResultScore<0.79)
                throw new Exception("The benchmark fixture did not render into the desktop capture");
            var samples=new List<double>();var cpuBefore=process.TotalProcessorTime;var workerBefore=GameFrameCapture.CurrentThreadCpuTicks();
            var wall=Stopwatch.StartNew();
            for(var i=0;i<40;i++)
            {
                var watch=Stopwatch.StartNew();
                if(!capture.TryCapture(game,requireForeground:false)) throw new Exception("Capture benchmark unavailable");
                detector.Analyze(capture.Pixels);samples.Add(watch.Elapsed.TotalMilliseconds);
                Thread.Sleep(Math.Max(0,250-(int)watch.Elapsed.TotalMilliseconds));
            }
            var cpu=process.TotalProcessorTime-cpuBefore;var worker=GameFrameCapture.CurrentThreadCpuTicks()-workerBefore;
            window.Close();app.Shutdown();var ordered=samples.Order().ToArray();process.Refresh();
            captureReport=new {source="1280x720 static fixture window; game not running",sampleCount=samples.Count,
                meanMilliseconds=samples.Average(),p95Milliseconds=ordered[(int)((ordered.Length-1)*0.95)],maxMilliseconds=samples.Max(),
                capturedResultScore=detector.Analyze(capture.Pixels).ResultScore,
                processCpuPercent=cpu.TotalMilliseconds/wall.Elapsed.TotalMilliseconds/Environment.ProcessorCount*100,
                workerCpuPercent=worker/10000d/wall.Elapsed.TotalMilliseconds/Environment.ProcessorCount*100,
                processWorkingSetMiB=process.WorkingSet64/1048576d};
        }
        var idleRoot=Path.Combine(root,"idle-monitor");Directory.CreateDirectory(idleRoot);
        var monitor=new MatchEndMonitor(idleRoot);monitor.Start();Thread.Sleep(6000);monitor.StopAsync().GetAwaiter().GetResult();
        var idle=JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(idleRoot,"match-detection.json")));
        var report=JsonSerializer.Serialize(new {fixtures,capture=captureReport,idle,at=DateTimeOffset.UtcNow},new JsonSerializerOptions {WriteIndented=true});
        File.WriteAllText(Path.Combine(root,"benchmark.json"),report);Console.WriteLine(report);
    }
    private static byte[] Load(string path,bool pad=false)
    {
        using var image=System.Drawing.Image.FromFile(path);
        using var bitmap=new Bitmap(640,360,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Black);graphics.InterpolationMode=InterpolationMode.HighQualityBilinear;
            var height=pad ? (int)Math.Round(image.Height*640d/image.Width) : 360;
            graphics.DrawImage(image,new Rectangle(0,0,640,height));
        }
        var rect=new Rectangle(0,0,640,64);var data=bitmap.LockBits(rect,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        var pixels=new byte[640*64*4];
        try {for(var row=0;row<64;row++)Marshal.Copy(data.Scan0+row*data.Stride,pixels,row*640*4,640*4);}
        finally {bitmap.UnlockBits(data);}
        return pixels;
    }
}
