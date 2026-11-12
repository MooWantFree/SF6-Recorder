using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Sf6Prototype;

public partial class OpponentWindow : Window
{
    private readonly string _opponent;
    private readonly string _signedInId;
    private readonly BucklerClient _client;
    private readonly ProfileCache _portraits;
    private readonly RankQuerySession _session;
    private readonly CancellationTokenSource _cancel=new();
    private OpponentProfile? _profile;
    private IReadOnlyList<CharacterRank> _ranks=[];
    private bool _busy,_changing,_closing,_suspended;
    private int _lastLpSeason=-1, _lastMrSeason=-1;
    public record SeasonChoice(int Id,int Current) { public override string ToString()=>Id==-1 ? "达到最高" : Id==Current ? $"赛季 {Id} · 当前" : $"赛季 {Id}"; }
    public record RankRow(CharacterRank Rank,ImageSource? Image,bool Peak)
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
    public OpponentWindow(Window owner,string opponent,string name,ConnectionSettings settings,SavedSession session,CookieVault vault,ProfileCache portraits)
    {
        InitializeComponent(); Owner=owner; _opponent=opponent; _signedInId=session.AccountId; _client=new(settings,session,vault); _portraits=portraits;
        _session=new(_client,opponent);
        PlayerName.Text=new PlayerIdentity(opponent,name).DisplayNameFor(_signedInId); Title=$"{PlayerName.Text} · 历史段位"; UidText.Text=$"UID {opponent}";
        Loaded+=async(_,_)=>await LoadAsync();
    }
    private void SetBusy(bool busy)
    {
        _busy=busy; RefreshButton.IsEnabled=!busy && !_suspended;
        SeasonPicker.IsEnabled=ModePicker.IsEnabled=!busy && !_suspended && _profile!=null;
        ShowUnranked.IsEnabled=!busy && _profile!=null;
        RefreshLabel.Text=busy ? "查询中…" : "刷新";
        var pending=_suspended && _profile==null;
        LoadingState.Visibility=busy || pending ? Visibility.Visible : Visibility.Collapsed;
        RankGrid.Visibility=busy || pending ? Visibility.Hidden : Visibility.Visible;
        if(pending) Status.Text="等待刷新完成";
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
                PlayerName.Text=new PlayerIdentity(_opponent,_profile.Name).DisplayNameFor(_signedInId); Title=$"{PlayerName.Text} · 历史段位";
                SetSeasons(season);
            }
            if(SeasonPicker.SelectedItem is not SeasonChoice choice) { EmptyText.Text="该赛季暂无 MR 数据"; EmptyText.Visibility=Visibility.Visible; return; }
            var result=await _session.GetAsync(new(choice.Id,ModePicker.SelectedIndex==1),_cancel.Token);
            _ranks=result.Characters;
            ShowRows(); Status.Text="更新于 "+result.FetchedAt.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss");
        }
        catch(OperationCanceledException) when(_cancel.IsCancellationRequested) { }
        catch(LoginRequiredException) { Status.Text="登录已失效，请关闭此窗口后重新登录。"; }
        catch(Exception) { Status.Text="暂时无法获取段位，请稍后重试。"; }
        finally { SetBusy(false); if(_closing) Close(); }
    }
    private void ShowRows()
    {
        var peak=ModePicker.SelectedIndex==1;
        MrColumn.Visibility=(SeasonPicker.SelectedItem as SeasonChoice)?.Id==-1 ? Visibility.Collapsed : Visibility.Visible;
        var rows=_ranks.Where(r=>ShowUnranked.IsChecked==true || (peak ? r.Mr>0 : r.Ranked))
            .OrderByDescending(r=>peak ? r.Mr : r.Lp).ThenBy(r=>r.CharacterId)
            .Select(r=>new RankRow(r,Pictures.Load(_portraits.CharacterImage(r.CharacterId,r.Name)),peak)).ToList();
        RankGrid.ItemsSource=rows; EmptyText.Text=peak ? "该赛季暂无 MR 数据" : "暂无段位记录"; EmptyText.Visibility=rows.Count==0 ? Visibility.Visible : Visibility.Collapsed;
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
        if(UidText.Text=="已复制") return;
        try { Clipboard.SetText(_opponent); UidText.Text="已复制"; await Task.Delay(1200); UidText.Text=$"UID {_opponent}"; }
        catch(System.Runtime.InteropServices.ExternalException) { Status.Text="剪贴板暂时不可用，请重试。"; }
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
