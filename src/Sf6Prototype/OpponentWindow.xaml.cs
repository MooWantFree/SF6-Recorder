using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Sf6Prototype;

public partial class OpponentWindow : ChromeWindow
{
    private readonly string _opponent;
    private readonly string _signedInId;
    private readonly string _initialName;
    private readonly BucklerClient _client;
    private readonly ProfileCache _portraits;
    private readonly RankQuerySession _session;
    private readonly CancellationTokenSource _cancel=new();
    private OpponentProfile? _profile;
    private IReadOnlyList<CharacterRank> _ranks=[];
    private bool _busy,_changing,_closing,_suspended;
    private int _lastLpSeason=-1, _lastMrSeason=-1;
    public bool QueryInProgress=>_busy;
    public record SeasonChoice(int Id,int Current) { public override string ToString()=>Id==-1 ? Strings.HighestReached : Id==Current ? Localization.Format(Strings.CurrentSeason, Id) : Localization.Format(Strings.SeasonNumber, Id); }
    public record RankRow(CharacterRank Rank,ImageSource? Image,bool Peak,string Name)
    { public string LpSuffix=>!Peak && Rank.Ranked && Rank.Lp>=0 ? $" ({Rank.Lp:0}lp)" : ""; }
    // The main sync shares the site's request budget, so queries pause while it runs.
    public bool Suspended
    {
        get=>_suspended;
        set
        {
            var resume=_suspended && !value;_suspended=value;SetBusy(_busy);
            if(resume && IsLoaded && _profile==null && !_busy && !_closing) _=LoadAsync();
        }
    }
    public OpponentWindow(Window owner,string opponent,string name,ConnectionSettings settings,SavedSession session,CookieVault vault,ProfileCache portraits,Action<CharacterObservation>? observeCharacters=null)
    {
        InitializeComponent();InitializeWindowChrome(WindowSurface,TitleBar,MaximizeButton);
        Owner=owner; _opponent=opponent; _signedInId=session.AccountId; _initialName=name; _client=new(settings,session,vault); _portraits=portraits;
        _client.CharacterObserver=observeCharacters;
        _session=new(_client,opponent);
        PlayerName.Text=new PlayerIdentity(opponent,name).DisplayNameFor(_signedInId); Title=Localization.Format(Strings.PlayerRankHistory, PlayerName.Text); UidText.Text=$"UID {opponent}";
        Loaded+=async(_,_)=>await LoadAsync();
    }
    private void SetBusy(bool busy)
    {
        _busy=busy; RefreshButton.IsEnabled=!busy && !_suspended;
        SeasonPicker.IsEnabled=ModePicker.IsEnabled=!busy && !_suspended && _profile!=null;
        ShowUnranked.IsEnabled=!busy && _profile!=null;
        RefreshLabel.Text=busy ? Strings.Searching : Strings.Refresh;
        var pending=_suspended && _profile==null;
        LoadingState.Visibility=busy || pending ? Visibility.Visible : Visibility.Collapsed;
        RankGrid.Visibility=busy || pending ? Visibility.Hidden : Visibility.Visible;
        if(pending) Status.Text=Strings.WaitingForRefresh;
        if(busy) EmptyText.Visibility=Visibility.Collapsed;
    }
    private void SetSeasons(int selected)
    {
        _changing=true;
        var peak=ModePicker.SelectedIndex==1;
        var seasons=_profile!.Seasons.Where(s=>!peak || s>0);
        if(!peak) seasons=seasons.Prepend(-1);
        var choices=seasons.Select(s=>new SeasonChoice(s,_profile.CurrentSeason)).ToList();
        SeasonPicker.ItemsSource=choices; SeasonPicker.SelectedItem=choices.FirstOrDefault(s=>s.Id==selected) ?? choices.FirstOrDefault();
        _changing=false;
    }
    private async Task LoadAsync(bool refresh=false)
    {
        if(_busy || _suspended) return; SetBusy(true); Status.Text=""; _ranks=[]; RankGrid.ItemsSource=null; EmptyText.Visibility=Visibility.Collapsed;
        try
        {
            var season=(SeasonPicker.SelectedItem as SeasonChoice)?.Id ?? -1;
            if(_profile==null || refresh)
            {
                _profile=await _session.LoadProfileAsync(_cancel.Token,refresh);
                PlayerName.Text=new PlayerIdentity(_opponent,_profile.Name).DisplayNameFor(_signedInId); Title=Localization.Format(Strings.PlayerRankHistory, PlayerName.Text);
                SetSeasons(season);
            }
            if(SeasonPicker.SelectedItem is not SeasonChoice choice) { EmptyText.Text=Strings.NoSeasonMr; EmptyText.Visibility=Visibility.Visible; return; }
            var result=await _session.GetAsync(new(choice.Id,ModePicker.SelectedIndex==1),_cancel.Token);
            _ranks=result.Characters;
            ShowRows(); Status.Text=Strings.UpdatedAtPrefix+result.FetchedAt.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss");
        }
        catch(OperationCanceledException) when(_cancel.IsCancellationRequested) { }
        catch(LoginRequiredException) { Status.Text=Strings.SessionExpiredCloseWindow; }
        catch(Exception) { Status.Text=Strings.RankLookupFailed; }
        finally { SetBusy(false); if(_closing) Close(); }
    }
    private void ShowRows()
    {
        var peak=ModePicker.SelectedIndex==1;
        MrColumn.Visibility=(SeasonPicker.SelectedItem as SeasonChoice)?.Id==-1 ? Visibility.Collapsed : Visibility.Visible;
        var rows=_ranks.Where(r=>ShowUnranked.IsChecked==true || (peak ? r.Mr>0 : r.Ranked))
            .OrderByDescending(r=>peak ? r.Mr : r.Lp).ThenBy(r=>r.CharacterId)
            .Select(r=>new RankRow(r,Pictures.Load(_portraits.CharacterImage(r.CharacterId,r.Name)),peak,_portraits.Names.Display(r.CharacterId,r.Name))).ToList();
        RankGrid.ItemsSource=rows; EmptyText.Text=peak ? Strings.NoSeasonMr : Strings.NoRankRecords; EmptyText.Visibility=rows.Count==0 ? Visibility.Visible : Visibility.Collapsed;
    }
    public void RefreshCharacterNames()=>ShowRows();
    public void RefreshLanguage()
    {
        PlayerName.Text=new PlayerIdentity(_opponent,_profile?.Name ?? _initialName).DisplayNameFor(_signedInId);
        Title=Localization.Format(Strings.PlayerRankHistory,PlayerName.Text);
        if(_profile!=null) SetSeasons((SeasonPicker.SelectedItem as SeasonChoice)?.Id ?? -1);
        Status.Text=""; ShowRows(); SetBusy(_busy);
    }
    private async void Selection_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_changing || _profile==null) return;
        if(SeasonPicker.SelectedItem is SeasonChoice choice)
        { if(ModePicker.SelectedIndex==1) _lastMrSeason=choice.Id; else _lastLpSeason=choice.Id; }
        await LoadAsync();
    }
    private async void Mode_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_changing || _profile==null) return;
        var season=ModePicker.SelectedIndex==1 ? (_lastMrSeason>0 ? _lastMrSeason : _profile.CurrentSeason) : _lastLpSeason;
        SetSeasons(season); await LoadAsync();
    }
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await LoadAsync(refresh:true);
    private void Season_DropDownOpened(object? sender,EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if(!SeasonPicker.IsDropDownOpen || SeasonPicker.Template.FindName("PART_Popup",SeasonPicker) is not Popup {Child: { } child}) return;
            child.UpdateLayout();
            var viewer=FindChild<ScrollViewer>(child);
            if(viewer==null || SeasonPicker.ItemContainerGenerator.ContainerFromIndex(SeasonPicker.SelectedIndex) is not FrameworkElement item) return;
            var middle=item.TranslatePoint(new Point(0,item.ActualHeight/2),viewer).Y+viewer.VerticalOffset;
            viewer.ScrollToVerticalOffset(ViewportMath.CenterOffset(middle,viewer.ViewportHeight,viewer.ScrollableHeight));
        },DispatcherPriority.ContextIdle);
    }
    private static T? FindChild<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is T found) return found;
            if(FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private void Unranked_Changed(object sender,RoutedEventArgs e) { if(_profile!=null && !_busy) ShowRows(); }
    private async void CopyUid_Click(object sender,RoutedEventArgs e)
    {
        if(UidText.Text==Strings.Copied) return;
        try { Clipboard.SetText(_opponent); UidText.Text=Strings.Copied; await Task.Delay(1200); UidText.Text=$"UID {_opponent}"; }
        catch(System.Runtime.InteropServices.ExternalException) { Status.Text=Strings.ClipboardUnavailable; }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if(_busy) { _closing=true; _cancel.Cancel(); e.Cancel=true; }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _cancel.Cancel(); _cancel.Dispose(); _client.Dispose(); _session.Clear(); _ranks=[]; _profile=null; RankGrid.ItemsSource=null; base.OnClosed(e);
    }
}
