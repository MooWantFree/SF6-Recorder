using Sf6Prototype;

public static class SettingsChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool condition,string message) { if(!condition) throw new Exception(message); count++; }
        void Reject(Action action,string message)
        {
            try { action(); } catch(ArgumentException) { count++; return; }
            throw new Exception(message);
        }
        var directory=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"settings.json");
        Check(ConnectionSettings.Load(directory) is {RefreshReminderSeconds:86400,AutoRefreshSeconds:0},"New installations default to a 24-hour reminder and no automatic requests");
        File.WriteAllText(path,"""{"UseProxy":true,"ProxyAddress":"http://127.0.0.1:8888","PlayerId":"1234567890"}""");
        var legacy=ConnectionSettings.Load(directory);
        Check(legacy.ApiIntervalSeconds==2 && new ConnectionSettings().ApiIntervalSeconds==2,"New settings and files without an explicit interval default to two seconds");
        foreach(var seconds in new[]{0.1,1,3,5,10,3600,2.5})
        {
            var setting=legacy with {ApiIntervalSeconds=ConnectionSettings.ParseApiIntervalSeconds(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture))};setting.Save(directory);
            Check(ConnectionSettings.Load(directory).ApiIntervalSeconds==seconds,"Decimal API intervals survive persistence: "+seconds);
        }
        foreach(var invalid in new[]{"","0","-1","0.01","3601","NaN","Infinity","1e3","1,5","+5","5s"})
            Reject(()=>ConnectionSettings.ParseApiIntervalSeconds(invalid),"Invalid API interval: "+invalid);
        Check(ConnectionSettings.LowApiInterval(0.1) && ConnectionSettings.LowApiInterval(1.99) && !ConnectionSettings.LowApiInterval(2) && !ConnectionSettings.LowApiInterval(3),"Only intervals below two seconds show a risk hint");
        var validApi=File.ReadAllText(path);
        Reject(()=>(legacy with {ApiIntervalSeconds=double.NaN}).Save(directory),"Invalid API intervals cannot overwrite saved settings");
        Check(File.ReadAllText(path)==validApi,"API interval validation preserves the settings file on failure");
        File.WriteAllText(path,"""{"ApiIntervalSeconds":0,"PlayerId":"1234567890"}""");
        Check(ConnectionSettings.Load(directory) is {ApiIntervalSeconds:2,PlayerId:"1234567890"},"Invalid stored API intervals fall back without losing the account");
        Check(legacy is {RefreshReminderSeconds:86400,AutoRefreshSeconds:0,UseProxy:true,ProxyAddress:"http://127.0.0.1:8888",PlayerId:"1234567890"},"Old settings gain safe interval defaults without losing connection or account preferences");
        var custom=legacy with {RefreshReminderSeconds=ConnectionSettings.ParseReminderSeconds("5")}; custom.Save(directory);
        Check(ConnectionSettings.Load(directory) is {RefreshReminderSeconds:5,UseProxy:true,PlayerId:"1234567890"},"A custom reminder survives save and restart alongside existing settings");
        foreach(var seconds in new[]{0,86400,43200,3600,600})
        {
            var changed=custom with {RefreshReminderSeconds=ConnectionSettings.ParseReminderSeconds(seconds.ToString())}; changed.Save(directory);
            Check(ConnectionSettings.Load(directory).RefreshReminderSeconds==seconds,$"Reminder preset {seconds} seconds persists");
        }
        Check(ConnectionSettings.ParseReminderSeconds(" 0005 ")==5 && ConnectionSettings.ParseReminderSeconds(int.MaxValue.ToString())==int.MaxValue,"Whole-second input accepts whitespace, leading zeros and the full supported integer range");
        foreach(var invalid in new[]{"","-1","1.5","NaN","+5","86400s","2147483648"})
        {
            Reject(()=>ConnectionSettings.ParseReminderSeconds(invalid),"Invalid reminder input must not be accepted: "+invalid);
            Reject(()=>ConnectionSettings.ParseAutoRefreshSeconds(invalid),"Invalid automatic refresh input must not be accepted: "+invalid);
        }
        foreach(var seconds in new[]{0,300,1200,3600,7})
        {
            var changed=custom with {RefreshReminderSeconds=0,AutoRefreshSeconds=ConnectionSettings.ParseAutoRefreshSeconds(seconds.ToString())}; changed.Save(directory);
            Check(ConnectionSettings.Load(directory) is {RefreshReminderSeconds:0} loaded && loaded.AutoRefreshSeconds==seconds,$"Automatic refresh interval {seconds} and a disabled reminder persist independently");
        }
        var before=File.ReadAllText(path);
        Reject(()=>(custom with {RefreshReminderSeconds=-1}).Save(directory),"Negative reminder intervals must not overwrite valid settings");
        Reject(()=>(custom with {AutoRefreshSeconds=-1}).Save(directory),"Negative automatic refresh intervals must not overwrite valid settings");
        Check(File.ReadAllText(path)==before,"Failed validation leaves the previous settings file intact");
        File.WriteAllText(path,"""{"RefreshReminderSeconds":-5,"AutoRefreshSeconds":-10,"PlayerId":"1234567890"}""");
        Check(ConnectionSettings.Load(directory) is {RefreshReminderSeconds:86400,AutoRefreshSeconds:0,PlayerId:"1234567890"},"Invalid persisted intervals fall back safely without resetting the account");
        var now=new DateTimeOffset(2026,11,12,12,0,0,TimeSpan.FromHours(8));
        Check(!DateRanges.IsRefreshOverdue(now.AddSeconds(-5),now,5) && DateRanges.IsRefreshOverdue(now.AddSeconds(-6),now,5),"A five-second custom interval uses seconds, not the former fixed 24-hour threshold");
        Check(!DateRanges.IsRefreshOverdue(now.AddHours(-2),now,86400) && DateRanges.IsRefreshOverdue(now.AddHours(-2),now,3600),"Changing the saved interval immediately changes whether the same player needs a reminder");
        Check(!DateRanges.IsRefreshOverdue(null,now,5),"Unknown last-update timestamps do not invent an overdue reminder");
        Check(!DateRanges.IsRefreshOverdue(now.AddYears(-1),now,0),"Zero disables the reminder regardless of data age");

        var schedule=new AutoRefreshSchedule();
        schedule.Configure(0,now);
        Check(schedule.DueAt==null && !schedule.TryBegin(now.AddYears(1),true),"Zero schedules no automatic requests, even after a long absence");
        schedule.Configure(300,now);
        Check(!schedule.TryBegin(now.AddSeconds(299),true) && schedule.TryBegin(now.AddSeconds(300),true),"Automatic refresh waits a complete configured interval");
        Check(!schedule.TryBegin(now.AddSeconds(300),true),"Duplicate timer ticks cannot launch the same refresh twice");
        Check(!schedule.TryBegin(now.AddSeconds(600),false) && schedule.IsDue(now.AddSeconds(601)),"Busy or unavailable sessions defer rather than drop a due refresh");
        Check(schedule.TryBegin(now.AddDays(1),true) && !schedule.TryBegin(now.AddDays(1),true),"Resume from sleep starts one run without a catch-up burst");
        schedule.Restart(now.AddDays(1).AddMinutes(2));
        Check(!schedule.IsDue(now.AddDays(1).AddMinutes(6)) && schedule.IsDue(now.AddDays(1).AddMinutes(7)),"The next interval starts after a long-running download finishes");
        schedule.Pause(); schedule.Restart(now);
        Check(!schedule.TryBegin(now.AddDays(1),true),"Login failures stay paused when a run completes");
        schedule.Resume(now);
        Check(schedule.TryBegin(now.AddMinutes(5),true),"Successful login or manual refresh resumes scheduling");
        schedule.Configure(1200,now);
        Check(!schedule.IsDue(now.AddMinutes(5)) && schedule.IsDue(now.AddMinutes(20)),"Saving a different interval replaces the previous deadline");
        schedule.Configure(0,now);
        Check(!schedule.IsDue(now.AddYears(1)),"Disabling an active timer cancels future runs");
        Reject(()=>schedule.Configure(-1,now),"A negative schedule cannot cause a rapid retry loop");
        Check(new ConnectionSettings().WindowCloseAction==CloseAction.Ask,"Closing a new installation asks instead of silently exiting or hiding");
        foreach(var action in Enum.GetValues<CloseAction>())
        {
            (custom with {WindowCloseAction=action}).Save(directory);
            Check(ConnectionSettings.Load(directory).WindowCloseAction==action,"Remembered close action survives restart: "+action);
        }
        File.WriteAllText(path,"""{"WindowCloseAction":99}""");
        Check(ConnectionSettings.Load(directory).WindowCloseAction==CloseAction.Ask,"Unknown persisted close actions fall back to asking");
        return count;
    }
}
