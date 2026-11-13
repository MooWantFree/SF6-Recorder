using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Shape = System.Windows.Shapes.Shape;

namespace Sf6Prototype;

public partial class MainWindow
{
    private MatchEndMonitor? _matchEndMonitor;
    private readonly MatchEndRefreshSchedule _matchEndRefresh=new();
    private readonly DispatcherTimer _matchEndRefreshTimer=new() {Interval=TimeSpan.FromSeconds(1)};
    private readonly DispatcherTimer _matchEndStatusTimer=new() {Interval=TimeSpan.FromSeconds(2)};
    private bool _sf6Running;
    private bool _matchEndTimerInitialized;
    private bool _matchEndRefreshInFlight;
    private AnimationClock? _matchEndStatusClock;
    private sealed record MatchEndFollowContext(string Owner,string Category,string? PreviousReplay,int NavigationVersion,int InteractionVersion);
    private void InitializeMatchEndStatus()
    {
        _matchEndStatusTimer.Tick+=(_,_)=>UpdateMatchEndStatus();
        PaintMatchEndStatus();
    }
    private void UpdateMatchEndStatus()
    {
        _sf6Running=_settings.RefreshAfterMatch && Sf6GameProbe.Find()!=null;
        PaintMatchEndStatus();
    }
    private void PaintMatchEndStatus()
    {
        var enabled=_settings.RefreshAfterMatch;
        var color=enabled ? _sf6Running ? "Win" : "Loss" : "Ink3";
        MatchEndStatusLight.SetResourceReference(Shape.StrokeProperty,color);
        if(enabled) MatchEndStatusLight.SetResourceReference(Shape.FillProperty,color);
        else MatchEndStatusLight.Fill=Brushes.Transparent;
        var state=enabled ? _sf6Running ? Strings.Sf6Running : Strings.Sf6NotRunning : Strings.Off;
        var description=$"{Strings.RefreshAfterMatchLabel}\n{state}";
        MatchEndStatus.ToolTip=description;
        AutomationProperties.SetName(MatchEndStatus,description);
        UpdateMatchEndStatusAnimation();
    }
    private void UpdateMatchEndStatusAnimation()
    {
        var animate=_settings.RefreshAfterMatch && !_exitRequested && SystemParameters.ClientAreaAnimation &&
            (_matchEndRefresh.Owner!=null || _matchEndRefreshInFlight);
        if(animate==(_matchEndStatusClock!=null)) return;
        if(!animate)
        {
            _matchEndStatusClock?.Controller?.Remove();_matchEndStatusClock=null;
            MatchEndStatusScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty,null);
            MatchEndStatusScale.ScaleY=1;return;
        }
        var motion=new DoubleAnimationUsingKeyFrames {Duration=TimeSpan.FromSeconds(1),RepeatBehavior=RepeatBehavior.Forever};
        motion.KeyFrames.Add(new LinearDoubleKeyFrame(1,KeyTime.FromTimeSpan(TimeSpan.Zero)));
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(.35,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)))
            {EasingFunction=new CubicEase {EasingMode=EasingMode.EaseInOut}});
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(1,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)))
            {EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut}});
        motion.KeyFrames.Add(new LinearDoubleKeyFrame(1,KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1))));
        _matchEndStatusClock=(AnimationClock)motion.CreateClock(true);
        MatchEndStatusScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty,_matchEndStatusClock);
    }
    private MatchEndFollowContext? CaptureMatchEndFollow(string owner) =>
        !_settings.FocusLatestMatchAfterRefresh || PageTabs.SelectedIndex!=0 || History.FavoritesOnly || _navigation.Current.Id!=owner ? null :
        new(owner,History.Category,_archive.All.FirstOrDefault()?.ReplayId,_navigationVersion,History.InteractionVersion);
    private void FollowRefreshedMatch(MatchEndFollowContext? follow)
    {
        if(follow==null || !CanFollow() || _archive.All.FirstOrDefault() is not { } latest ||
            latest.OwnerId!=follow.Owner || latest.ReplayId==follow.PreviousReplay ||
            follow.Category.Length>0 && latest.Category!=follow.Category) return;
        History.RevealLatestMatch(latest.OwnerId,latest.ReplayId,follow.InteractionVersion,CanFollow);
        bool CanFollow()=>_settings.RefreshAfterMatch && _settings.FocusLatestMatchAfterRefresh && !_exitRequested && _navigationVersion==follow.NavigationVersion &&
            _navigation.Current.Id==follow.Owner && PageTabs.SelectedIndex==0 && !History.FavoritesOnly && History.Category==follow.Category;
    }
    private async Task ConfigureMatchEndDetectionAsync()
    {
        _matchEndStatusTimer.Stop();
        UpdateMatchEndStatus();
        UpdateRecentMatches();
        if(_settings.RefreshAfterMatch && !_exitRequested && !App.Capture) _matchEndStatusTimer.Start();
        if(!_matchEndTimerInitialized)
        {_matchEndRefreshTimer.Tick+=MatchEndRefresh_Tick;_matchEndTimerInitialized=true;}
        if(App.Capture || !_settings.RefreshAfterMatch || _exitRequested)
        {
            _matchEndRefreshTimer.Stop();_matchEndRefresh.Clear();
            UpdateMatchEndStatusAnimation();
            if(_matchEndMonitor!=null) await _matchEndMonitor.StopAsync();
            return;
        }
        if(_matchEndMonitor==null)
        {
            _matchEndMonitor=new(App.DataDirectory);
            _matchEndMonitor.MatchEnded+=at=>
            {
                if(Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(()=>QueueMatchEndRefresh(at),DispatcherPriority.Background);
            };
        }
        _matchEndMonitor.SetFrequency(_settings.MatchEndSamplesPerSecond);
        _matchEndMonitor.Start();_matchEndRefreshTimer.Start();
    }
    private void QueueMatchEndRefresh(DateTimeOffset at)
    {
        if(!_ready || !_settings.RefreshAfterMatch || _closeRequested || App.Capture) return;
        var session=_vault.Load();
        if(session==null || _autoRefresh.Paused)
        {_matchEndMonitor?.ReportRefresh("sign_in_required");return;}
        _matchEndRefresh.Queue(session.AccountId,at,
            _settings.MatchEndWaitSeconds,_settings.MatchEndRetrySeconds);
        UpdateMatchEndStatusAnimation();
        _matchEndMonitor?.ReportRefresh("waiting");
    }
    private async void MatchEndRefresh_Tick(object? sender,EventArgs e)
    {
        if(_matchEndRefreshInFlight || _matchEndRefresh.Owner==null || !_ready || App.Capture || !_settings.RefreshAfterMatch || _closeRequested) return;
        var version=_matchEndRefresh.Version;
        try
        {
            var now=DateTimeOffset.UtcNow;var session=_vault.Load();
            if(_busy || _savingSettings || _restoringView || _stoppingRefresh || !IsEnabled ||
                OwnedWindows.Count>0 || AccountPopup.IsOpen || PlayerSearchPopup.IsOpen) return;
            if(!_matchEndRefresh.IsDue(session?.AccountId,now)) return;
            if(_autoRefresh.Paused) {_matchEndRefresh.Clear();return;}
            var boundary=_store.Frontier(session!.AccountId,BattlePager.AccountBoundary);
            var follow=CaptureMatchEndFollow(session!.AccountId);
            version=_matchEndRefresh.Begin();_matchEndMonitor?.ReportRefresh("refreshing");
            _matchEndRefreshInFlight=true;
            var task=AutoRefreshAccountAsync(session!);_foregroundTask=task;
            var success=await task;
            var changed=_store.Frontier(session!.AccountId,BattlePager.AccountBoundary)!=boundary;
            if(success && changed) FollowRefreshedMatch(follow);
            if(_matchEndRefresh.Version!=version) return;
            _matchEndMonitor?.ReportRefresh(!success ? "refresh_failed" : changed ? "new_matches" :
                _matchEndRefresh.Attempts>=2 ? "no_new_matches" : "waiting_for_retry");
            _matchEndRefresh.Complete(version,success,changed,DateTimeOffset.UtcNow);
        }
        catch(Exception ex)
        {
            if(_matchEndRefresh.Complete(version,false,false,DateTimeOffset.UtcNow)) _matchEndMonitor?.ReportRefresh("refresh_failed");
            ReportError(ex);
        }
        finally {_matchEndRefreshInFlight=false;UpdateMatchEndStatusAnimation();}
    }
    private async Task StopMatchEndDetectionAsync()
    {
        _matchEndStatusTimer.Stop();
        _matchEndRefreshTimer.Stop();_matchEndRefresh.Clear();
        UpdateMatchEndStatusAnimation();
        if(_matchEndMonitor!=null) await _matchEndMonitor.StopAsync();
    }
}
