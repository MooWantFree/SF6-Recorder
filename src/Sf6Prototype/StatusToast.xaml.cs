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
    public bool IsLoading {get;private set;}
    public StatusToast()
    {
        InitializeComponent();
        _timer.Tick+=(_,_)=>Dismiss();
        Unloaded+=(_,_)=>_timer.Stop();
        MouseLeftButtonUp+=(_,e)=>{if(e.OriginalSource is not Button) Dismiss();};
    }
    public void Show(string text,bool danger=false,string? actionText=null,Action? action=null)
    {
        if(IsLoading) return;
        Present(text,danger,actionText,action);
        Progress.Visibility=Visibility.Collapsed;
        _timer.Start();
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
        AutomationProperties.SetHelpText(MessageButton,IsLoading ? "" : Strings.DismissNotice);
        UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    public void Dismiss()
    {
        if(IsLoading) return;
        _timer.Stop();Visibility=Visibility.Collapsed;_action=null;
        Progress.IsIndeterminate=false;
    }
    private void Dismiss_Click(object sender,RoutedEventArgs e) {Dismiss();e.Handled=true;}
    private void Action_Click(object sender,RoutedEventArgs e)
    {
        if(IsLoading) return;
        var action=_action;Dismiss();e.Handled=true;action?.Invoke();
    }
}
