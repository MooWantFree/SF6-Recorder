using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Sf6Prototype;

public partial class MainWindow
{
    private readonly RefreshProgressState _refreshProgress=new();
    private readonly DispatcherTimer _progressTimer=new() {Interval=TimeSpan.FromMilliseconds(200)};
    private readonly Stopwatch _progressElapsed=new(),_requestElapsed=new();
    private readonly TextBlock _progressTip=new();
    private bool _progressVisible,_progressFinished;
    private long _progressFinishedAt;

    private void InitializeRefreshProgress()
    {
        _progressTimer.Tick+=(_,_)=>
        {
            if(_progressFinished && Stopwatch.GetElapsedTime(_progressFinishedAt).TotalMilliseconds>=600) HideButtonProgress();
            else UpdateProgressTooltip();
        };
    }
    private void BeginButtonProgress()
    {
        _progressFinished=false;_progressVisible=true;
        _progressElapsed.Restart();_requestElapsed.Reset();SyncRing.Reset();
        SyncIcon.Visibility=Visibility.Collapsed;SyncRing.Visibility=Visibility.Visible;
        SyncButton.ToolTip=_progressTip;
        ToolTipService.SetShowDuration(SyncButton,int.MaxValue);
        UpdateButtonProgress();_progressTimer.Start();
    }
    private void HideButtonProgress()
    {
        _progressVisible=_progressFinished=false;_progressTimer.Stop();_progressElapsed.Stop();_requestElapsed.Stop();
        SyncRing.Reset();SyncRing.Visibility=Visibility.Collapsed;SyncIcon.Visibility=Visibility.Visible;
        CancelButton.Visibility=Visibility.Hidden;
        UpdateRefreshAction();AutomationProperties.SetHelpText(SyncButton,"");
        UpdateRefreshReminder();
    }
    private void UpdateRefreshAction()
    {
        AutomationProperties.SetName(SyncButton,_busy ? Strings.StopDownload : Strings.Refresh);
        if(!_progressVisible) SyncButton.ToolTip=_busy ? Strings.StopDownloadShortcut : Strings.RefreshShortcut;
    }
    private void ProgressStage(string stage)
    {_refreshProgress.SetStage(stage);UpdateButtonProgress();}
    private void RequestProgress(RequestActivity activity)
    {
        if(!_busy)return;
        // Local paging and request-gate waits must not impersonate a network refresh.
        if(!_progressVisible && activity.Phase==RequestPhase.Sending)
        {
            BeginButtonProgress();
            CancelButton.Visibility=Visibility.Visible;
            UpdateRefreshReminder();
        }
        _refreshProgress.Observe(activity);
        if(!_progressVisible)return;
        if(activity.Phase==RequestPhase.Sending)_requestElapsed.Restart();
        UpdateButtonProgress();
    }
    private void UpdateButtonProgress()
    {
        if(!_progressVisible)return;
        SyncRing.Update(_refreshProgress.Fraction);UpdateProgressTooltip();
    }
    private void UpdateProgressTooltip()
    {
        if(!_progressVisible)return;
        _progressTip.Text=_refreshProgress.Tooltip(_progressElapsed.Elapsed,RequestGate.Shared.Interval,RequestGate.Shared.RemainingDelay,_requestElapsed.Elapsed);
        if(_busy && !_progressFinished) _progressTip.Text+=Strings.ClickAgainToStop;
        AutomationProperties.SetHelpText(SyncButton,_progressTip.Text);
    }
    private void FinishButtonProgress(bool success,bool cancelled)
    {
        if(!_progressVisible)return;
        _refreshProgress.Finish(success,cancelled);_progressElapsed.Stop();
        if(!success) {HideButtonProgress();return;}
        _progressFinished=true;_progressFinishedAt=Stopwatch.GetTimestamp();UpdateButtonProgress();
    }
}
