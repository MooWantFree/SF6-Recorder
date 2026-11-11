using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Sf6Prototype;

public partial class OpponentWindow : Window
{
    private readonly string _opponent;
    private readonly BucklerClient _client;
    private readonly ProfileCache _portraits;
    private readonly CancellationTokenSource _cancel=new();
    private OpponentProfile? _profile;
    private IReadOnlyList<CharacterRank> _ranks=[];
    private bool _busy,_changing,_closing;
    public record SeasonChoice(int Id,int Current) { public override string ToString()=>Id==Current ? $"赛季 {Id} · 当前" : $"赛季 {Id}"; }
    public record RankRow(CharacterRank Rank,ImageSource? Image,bool Peak) { public string LpText=>Peak ? "—" : Rank.LpText; }
    public OpponentWindow(Window owner,string opponent,string name,ConnectionSettings settings,SavedSession session,CookieVault vault,ProfileCache portraits)
    {
        InitializeComponent(); Owner=owner; _opponent=opponent; _client=new(settings,session,vault); _portraits=portraits;
        PlayerName.Text=name; UidButton.Content=$"UID {opponent}  ·  复制";
        Loaded+=async(_,_)=>await LoadAsync(true);
    }
    private void SetBusy(bool busy)
    {
        _busy=busy; RefreshButton.IsEnabled=!busy;
        SeasonPicker.IsEnabled=ModePicker.IsEnabled=ShowUnranked.IsEnabled=!busy && _profile!=null;
        RefreshButton.Content=busy ? "查询中…" : "刷新";
    }
    private void SetSeasons(int selected)
    {
        _changing=true;
        var choices=_profile!.Seasons.Where(s=>ModePicker.SelectedIndex!=1 || s>0).Select(s=>new SeasonChoice(s,_profile.CurrentSeason)).ToList();
        SeasonPicker.ItemsSource=choices; SeasonPicker.SelectedItem=choices.FirstOrDefault(s=>s.Id==selected) ?? choices.FirstOrDefault();
        _changing=false;
    }
    private async Task LoadAsync(bool initial=false)
    {
        if(_busy) return; SetBusy(true); Status.Text=""; _ranks=[]; RankGrid.ItemsSource=null; EmptyText.Visibility=Visibility.Collapsed;
        try
        {
            var season=(SeasonPicker.SelectedItem as SeasonChoice)?.Id;
            if(initial || _profile==null || season==_profile.CurrentSeason && ModePicker.SelectedIndex==0)
            {
                var json=await _client.FetchPlayAsync(_opponent,_cancel.Token);
                _profile=RankProfiles.ParseProfile(json,_opponent); PlayerName.Text=_profile.Name; _ranks=_profile.Characters;
                SetSeasons(initial ? _profile.CurrentSeason : season ?? _profile.CurrentSeason);
                // A season can roll over while this window is open. Keep the selected season
                // and query its records instead of labeling the new season with the old ID.
                if(!initial && season.HasValue && season.Value!=_profile.CurrentSeason)
                    _ranks=RankProfiles.ParseSeason(await _client.FetchSeasonRanksAsync(_opponent,season.Value,false,_cancel.Token));
            }
            else
            {
                if(season==null) { EmptyText.Text="该赛季暂无 MR 数据"; EmptyText.Visibility=Visibility.Visible; return; }
                _ranks=RankProfiles.ParseSeason(await _client.FetchSeasonRanksAsync(_opponent,season.Value,ModePicker.SelectedIndex==1,_cancel.Token));
            }
            ShowRows(); Status.Text="更新于 "+DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss");
        }
        catch(OperationCanceledException) when(_cancel.IsCancellationRequested) { }
        catch(LoginRequiredException) { Status.Text="登录已失效，请关闭此窗口后重新登录。"; }
        catch(Exception) { Status.Text="暂时无法获取段位，请稍后重试。"; }
        finally { SetBusy(false); if(_closing) Close(); }
    }
    private void ShowRows()
    {
        var peak=ModePicker.SelectedIndex==1;
        var rows=_ranks.Where(r=>ShowUnranked.IsChecked==true || (peak ? r.Mr>0 : r.Ranked))
            .OrderByDescending(r=>peak ? r.Mr : r.Lp).ThenBy(r=>r.CharacterId)
            .Select(r=>new RankRow(r,Pictures.Load(_portraits.CharacterImage(r.CharacterId,r.Name)),peak)).ToList();
        RankGrid.ItemsSource=rows; EmptyText.Text=peak ? "该赛季暂无 MR 数据" : "该赛季暂无段位记录"; EmptyText.Visibility=rows.Count==0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void Selection_Changed(object sender,SelectionChangedEventArgs e) { if(!_changing && _profile!=null) await LoadAsync(); }
    private async void Mode_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(_changing || _profile==null) return;
        var season=(SeasonPicker.SelectedItem as SeasonChoice)?.Id ?? _profile.CurrentSeason; SetSeasons(season); await LoadAsync();
    }
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await LoadAsync();
    private void Unranked_Changed(object sender,RoutedEventArgs e) { if(_profile!=null && !_busy) ShowRows(); }
    private async void CopyUid_Click(object sender,RoutedEventArgs e)
    {
        try { Clipboard.SetText(_opponent); UidButton.Content="已复制"; await Task.Delay(1200); UidButton.Content=$"UID {_opponent}  ·  复制"; }
        catch(System.Runtime.InteropServices.ExternalException) { Status.Text="剪贴板暂时不可用，请重试。"; }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if(_busy) { _closing=true; _cancel.Cancel(); e.Cancel=true; }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _cancel.Cancel(); _cancel.Dispose(); _client.Dispose(); _ranks=[]; _profile=null; RankGrid.ItemsSource=null; base.OnClosed(e);
    }
}
