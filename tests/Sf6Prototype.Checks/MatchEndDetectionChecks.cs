using Sf6Prototype;

public static class MatchEndDetectionChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        var now=DateTimeOffset.UtcNow;var detector=new MatchEndDetection();
        var battle=new MatchFrameObservation(true,0.1);var result=new MatchFrameObservation(false,0.89);
        Check(!detector.Observe(result,now) && !detector.Observe(result,now.AddSeconds(1)),"Starting on an old result does not cause an official-site request");
        for(var i=0;i<3;i++) Check(!detector.Observe(battle,now.AddSeconds(i)),"Battle samples only arm the detector");
        Check(detector.Armed,"Three battle HUD samples arm one match");
        Check(!detector.Observe(new(false,0),now.AddSeconds(4)) && detector.Armed,"KO, loading and other unrecognized screens do not end the match");
        Check(!detector.Observe(result,now.AddSeconds(5)),"One result frame cannot trigger a refresh");
        detector.Suspend();
        Check(!detector.Observe(result,now.AddSeconds(6)),"Switching away interrupts result confirmation");
        Check(detector.Observe(result,now.AddSeconds(7)) && !detector.Armed,"A confirmed result emits once and disarms");
        Check(!detector.Observe(result,now.AddSeconds(8)) && !detector.Observe(result,now.AddSeconds(9)),"A persistent result never repeats its match-end event");
        for(var i=0;i<3;i++) detector.Observe(battle,now.AddSeconds(10+i));
        Check(!detector.Observe(result,now.AddMinutes(6)) && !detector.Armed,"Old battle evidence expires after a long absence");
        detector.Reset();detector.Observe(battle,now);detector.Suspend();detector.Observe(battle,now);
        Check(!detector.Armed,"Separated single HUD frames cannot arm the detector");
        var blank=new byte[MatchEndDetection.FrameWidth*MatchEndDetection.FrameHeight*4];
        Check(detector.Analyze(blank)==new MatchFrameObservation(false,0),"Black or unavailable captures do not look like a game result");
        var strong=new MatchFrameObservation(false,0.98);
        Check(!detector.Observe(strong,now),"Even a very strong stale result cannot trigger before a battle");
        for(var i=0;i<3;i++) detector.Observe(battle,now.AddMilliseconds(i*250));
        Check(detector.Observe(strong,now.AddSeconds(1)),"A high-confidence result survives a quick rematch that exposes just one sample");
        Check(!detector.Observe(strong,now.AddSeconds(2)),"A single-frame confirmation still emits exactly one event");
        for(var i=0;i<3;i++) detector.Observe(battle,now.AddSeconds(3+i));
        Check(detector.Observe(new(true,0.98),now.AddSeconds(6)),"A verified header takes precedence over the coarse HUD color heuristic on a result background");

        var schedule=new MatchEndRefreshSchedule();
        schedule.Queue("a",now);
        Check(!schedule.IsDue("a",now.AddSeconds(4)) && schedule.IsDue("a",now.AddSeconds(5)),"The default match-end wait is five seconds");
        schedule.Complete(schedule.Begin(),true,false,now.AddSeconds(7));
        Check(!schedule.IsDue("a",now.AddSeconds(14)) && schedule.IsDue("a",now.AddSeconds(15)),"The default retry waits eight seconds after the first completed refresh");
        schedule.Complete(schedule.Begin(),true,false,now.AddSeconds(16));
        Check(schedule.Owner==null,"No new record after the one retry stops further requests");
        schedule.Queue("a",now,0,30);
        Check(schedule.IsDue("a",now),"A custom zero wait is immediate");
        schedule.Complete(schedule.Begin(),true,false,now);
        Check(!schedule.IsDue("a",now.AddSeconds(29)) && schedule.IsDue("a",now.AddSeconds(30)),"Custom retry intervals are honored");
        schedule.Queue("a",now);
        Check(!schedule.IsDue("b",now.AddMinutes(1)) && schedule.Owner==null,"Switching accounts discards pending work for the old account");
        schedule.Queue("a",now);
        Check(schedule.Complete(schedule.Begin(),true,true,now.AddSeconds(6)) && schedule.Owner==null,"A refresh that finds new matches completes its own detection task");
        schedule.Queue("a",now);schedule.Complete(schedule.Begin(),false,false,now);
        Check(schedule.Owner==null,"Failed or cancelled refreshes do not silently retry");
        schedule.Queue("a",now,3600,3600);
        Check(schedule.IsDue("a",now.AddHours(1)),"Long valid waits do not expire before their deadline");
        schedule.Clear();Check(!schedule.IsDue("a",now.AddYears(1)),"Disabling or stopping cancels future requests");

        var directory=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        Check(new ConnectionSettings() is {RefreshAfterMatch:false,MatchEndWaitSeconds:5,MatchEndRetrySeconds:8},"New installations do not capture or send match-end requests automatically");
        var settings=new ConnectionSettings {RefreshAfterMatch=true,MatchEndWaitSeconds=12,MatchEndRetrySeconds=20};settings.Save(directory);
        Check(ConnectionSettings.Load(directory) is {RefreshAfterMatch:true,MatchEndWaitSeconds:12,MatchEndRetrySeconds:20},"The switch and custom timing settings persist independently");
        File.WriteAllText(Path.Combine(directory,"settings.json"),"""{"MatchEndWaitSeconds":-1,"MatchEndRetrySeconds":0,"RefreshAfterMatch":true}""");
        Check(ConnectionSettings.Load(directory) is {RefreshAfterMatch:true,MatchEndWaitSeconds:5,MatchEndRetrySeconds:8},"Invalid stored timing values fall back without losing the switch");
        Check(ConnectionSettings.ParseMatchEndSeconds("0")==0 && ConnectionSettings.ParseMatchEndSeconds("3600",true)==3600,"Supported timing limits parse");
        (settings with {MatchEndSamplesPerSecond=2.5}).Save(directory);
        Check(ConnectionSettings.Load(directory).MatchEndSamplesPerSecond==2.5,"A custom fractional sampling frequency persists");
        Check(ConnectionSettings.ParseMatchEndFrequency("0.5")==0.5 && ConnectionSettings.ParseMatchEndFrequency("10")==10,"Sampling frequency accepts the supported limits");
        foreach(var text in new[]{"0","0.49","10.1","NaN","Infinity","1,5"})
        {
            try {ConnectionSettings.ParseMatchEndFrequency(text);throw new Exception("Invalid sampling frequency accepted");}
            catch(ArgumentException) {count++;}
        }
        foreach(var (text,retry) in new[]{("-1",false),("3601",false),("0",true),("1.5",true)})
        {
            try {ConnectionSettings.ParseMatchEndSeconds(text,retry);throw new Exception("Invalid match timing accepted");}
            catch(ArgumentException) {count++;}
        }
        return count;
    }
}
