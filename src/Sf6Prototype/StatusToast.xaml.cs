using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Sf6Prototype;

public partial class StatusToast : UserControl
{
    private readonly DispatcherTimer _timer=new() {Interval=TimeSpan.FromSeconds(5)};
    private Action? _action;
    private (UpdateRelease Release, Action<UpdateReminder> Save)? _updatePrompt;
    public bool IsLoading {get;private set;}
    public StatusToast()
    {
        InitializeComponent();
        _timer.Tick+=(_,_)=>Dismiss();
        Unloaded+=(_,_)=>_timer.Stop();
        MouseLeftButtonUp+=(_,e)=>{if(_updatePrompt==null && e.OriginalSource is not Button) Dismiss();};
    }
    public void Show(string text,bool danger=false,string? actionText=null,Action? action=null)
    {
        if(IsLoading) return;
        Present(text,danger,actionText,action);
        Progress.Visibility=Visibility.Collapsed;
        _timer.Start();
    }
    public void ShowUpdatePrompt(UpdateRelease release, Action install, Action<UpdateReminder> saveReminder)
    {
        if (IsLoading) return;
        Present(release.AvailableMessage, false, null, null);
        _action = install;
        _updatePrompt = (release, saveReminder);
        MessageButton.IsHitTestVisible = MessageButton.Focusable = false;
        MessageButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetHelpText(MessageButton, "");
        UpdateActions.Visibility = ClosePromptButton.Visibility = Visibility.Visible;
        Progress.Visibility = Visibility.Collapsed;
    }
    public void RefreshUpdatePrompt()
    {
        if (_updatePrompt is { } prompt) SetText(prompt.Release.AvailableMessage);
    }
    private void DeferUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updatePrompt is not { } prompt) return;
        e.Handled = true;
        var reminder = sender == IgnoreUpdateButton ? new UpdateReminder(IgnoredVersion: prompt.Release.Version.ToString(3)) :
            UpdateReminder.InFiveDays(DateTimeOffset.UtcNow);
        try { prompt.Save(reminder); Dismiss(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Diagnostics.Write("updates", "reminder_save_failed", error);
            Show(Strings.UpdateReminderSaveFailed, true);
        }
    }
    public void BeginProgress(string text)
    {
        IsLoading=true;
        Present(text,false,null,null);
        Progress.Visibility=Visibility.Visible;
        Progress.IsIndeterminate=SystemParameters.ClientAreaAnimation;
        Progress.Value=0;
    }
    public void UpdateProgress(string text)
    {
        if(IsLoading) SetText(text);
    }
    public void CompleteProgress(string text,bool danger=false,string? actionText=null,Action? action=null)
    {
        IsLoading=false;
        Present(text,danger,actionText,action);
        Progress.IsIndeterminate=false;
        Progress.Value=1;
        Progress.Visibility=Visibility.Collapsed;
        _timer.Start();
    }
    private void Present(string text,bool danger,string? actionText,Action? action)
    {
        _timer.Stop();
        Surface.SetResourceReference(BackgroundProperty,danger ? "DangerSoft" : "WinSoft");
        MessageText.SetResourceReference(TextBlock.ForegroundProperty,danger ? "Loss" : "Ink");
        _action=action;ActionButton.Content=actionText;
        ActionButton.Visibility=action==null ? Visibility.Collapsed : Visibility.Visible;
        _updatePrompt=null;
        UpdateActions.Visibility=ClosePromptButton.Visibility=Visibility.Collapsed;
        MessageButton.IsHitTestVisible=true;
        MessageButton.HorizontalContentAlignment=HorizontalAlignment.Center;
        MessageButton.Cursor=IsLoading ? Cursors.Arrow : Cursors.Hand;
        MessageButton.Focusable=!IsLoading;
        Visibility=Visibility.Visible;
        SetText(text);
    }
    private void SetText(string text)
    {
        MessageText.Text=text;
        AutomationProperties.SetName(this,text);
        AutomationProperties.SetName(MessageButton,text);
        AutomationProperties.SetHelpText(MessageButton,IsLoading || _updatePrompt!=null ? "" : Strings.DismissNotice);
        UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    public void Dismiss()
    {
        if(IsLoading) return;
        _timer.Stop();Visibility=Visibility.Collapsed;_action=null;_updatePrompt=null;
        Progress.IsIndeterminate=false;
    }
    private void Dismiss_Click(object sender,RoutedEventArgs e) {Dismiss();e.Handled=true;}
    private void Action_Click(object sender,RoutedEventArgs e)
    {
        if(IsLoading) return;
        var action=_action;Dismiss();e.Handled=true;action?.Invoke();
    }
}
