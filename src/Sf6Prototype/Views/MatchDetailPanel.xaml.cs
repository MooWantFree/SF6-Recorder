using System.Windows;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Sf6Prototype;

public partial class MatchDetailPanel : UserControl
{
    public event Action<PlayerIdentity>? RanksRequested;
    public event Action<PlayerIdentity>? PlayerRequested;
    public event Action<string>? Notice;
    public MatchListRow? Row => DataContext as MatchListRow;
    private Archive? _archive;
    private readonly ObservableCollection<HistoryMatchRow> _historyRows=[];
    private bool _animateHistory;
    private bool _towardNewer;
    private DispatcherOperation? _centerOperation;
    private int _scrollAnimationVersion;
    private static readonly DependencyProperty AnimatedOffsetProperty=DependencyProperty.RegisterAttached(
        "AnimatedOffset",typeof(double),typeof(MatchDetailPanel),new PropertyMetadata(0d,(d,e)=>((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));
    public void Bind(Archive archive) => _archive=archive;
    public MatchDetailPanel() { InitializeComponent(); RecentMatches.ItemsSource=_historyRows; Unloaded+=(_,_)=>CancelHistoryMotion(); }
    public void Show(MatchListRow? row, bool animateHistory = false)
    {
        var sameMatch = row != null && Row?.Battle.OwnerId == row.Battle.OwnerId && Row?.Battle.ReplayId == row.Battle.ReplayId;
        var samePair=row!=null && Row?.Battle.OwnerId==row.Battle.OwnerId && Row?.Battle.OpponentId==row.Battle.OpponentId;
        if(!sameMatch)
        {
            var oldIndex=_historyRows.ToList().FindIndex(r=>r.IsCurrent);
            var newIndex=_historyRows.ToList().FindIndex(r=>r.Battle.ReplayId==row?.Battle.ReplayId);
            _towardNewer=animateHistory && samePair && newIndex>=0 && oldIndex>=0 && newIndex<oldIndex;
        }
        DataContext = row;
        EmptyText.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
        Details.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
        if(row==null) { CancelHistoryMotion(); _historyRows.Clear(); HistorySection.Visibility=Visibility.Collapsed; return; }
        if(!samePair) { CancelHistoryMotion(); HistoryScroll.ScrollToTop(); }
        ResultBadge.Background=Brush(row.Kind switch {"win"=>"Win","loss"=>"Loss",_=>"Neutral"});
        var history=OpponentHistory.From(_archive?.All ?? [],row.Battle.OwnerId,row.Battle.OpponentId);
        HistorySection.Visibility=history.Total>0 ? Visibility.Visible : Visibility.Collapsed;
        var countChanged=_historyRows.Count!=history.Matches.Count;
        var rows=history.Matches.Select(b=>new HistoryMatchRow(b,b.ReplayId==row.Battle.ReplayId)).ToList();
        IncrementalList.Apply(_historyRows,rows,r=>(r.Battle.OwnerId,r.Battle.ReplayId),(old,next)=>old.Update(next));
        if(history.Total>0)
        {
            HistorySection.Visibility=Visibility.Visible;
            HistorySummary.Text=history.Summary;
            HistoryWinRate.Text=history.WinRate=="—" ? "" : $"胜率 {history.WinRate}";
            if(!sameMatch || !samePair || countChanged) QueueHistoryCenter(animateHistory && samePair);
        }
    }
    private void HistoryMatch_Click(object sender, RoutedEventArgs e)
    {
        if(sender is not Button {Tag:Battle selected} || _archive==null || Row is not { } current ||
            selected.OwnerId!=_archive.Owner || selected.OwnerId!=current.Battle.OwnerId || selected.OpponentId!=current.Battle.OpponentId) return;
        var battle=_archive.All.FirstOrDefault(b=>b.OwnerId==selected.OwnerId && b.ReplayId==selected.ReplayId);
        // This is detail-only navigation: leave the main list and chart selections intact.
        if(battle!=null) Show(_archive.Row(battle),animateHistory:true);
    }
    private void HistoryScroll_SizeChanged(object sender,SizeChangedEventArgs e) => QueueHistoryCenter(false);
    private void HistoryScroll_MouseWheel(object sender,MouseWheelEventArgs e) => CancelHistoryMotion();
    private void HistoryScroll_MouseDown(object sender,MouseButtonEventArgs e) => CancelHistoryMotion();
    private void CancelHistoryMotion()
    {
        _centerOperation?.Abort(); _centerOperation=null; _animateHistory=false;
        StopHistoryAnimation();
    }
    private void StopHistoryAnimation()
    {
        _scrollAnimationVersion++;
        var offset=HistoryScroll.VerticalOffset;
        HistoryScroll.BeginAnimation(AnimatedOffsetProperty,null);
        HistoryScroll.SetValue(AnimatedOffsetProperty,offset);
    }
    private void QueueHistoryCenter(bool animate)
    {
        _animateHistory|=animate;
        if(_centerOperation is {Status:DispatcherOperationStatus.Pending}) return;
        _centerOperation=Dispatcher.BeginInvoke(() =>
        {
            _centerOperation=null;
            var useAnimation=_animateHistory; _animateHistory=false;
            if(!IsVisible || _historyRows.FirstOrDefault(r=>r.IsCurrent) is not { } current) return;
            HistoryScroll.UpdateLayout();
            if(RecentMatches.ItemContainerGenerator.ContainerFromItem(current) is not FrameworkElement item) return;
            var top=item.TranslatePoint(new Point(0,0),HistoryScroll).Y+HistoryScroll.VerticalOffset;
            // Leave more context ahead in the direction the user is browsing.
            var target=ViewportMath.HistoryOffset(top,item.ActualHeight,HistoryScroll.ViewportHeight,HistoryScroll.ScrollableHeight,_towardNewer);
            var from=HistoryScroll.VerticalOffset;
            StopHistoryAnimation();
            if(!useAnimation || !SystemParameters.ClientAreaAnimation || Math.Abs(target-from)<1)
            { HistoryScroll.SetValue(AnimatedOffsetProperty,target); return; }
            var version=_scrollAnimationVersion;
            var animation=new DoubleAnimation(from,target,TimeSpan.FromMilliseconds(220)) {EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut}};
            animation.Completed+=(_,_)=>
            {
                if(version!=_scrollAnimationVersion) return;
                HistoryScroll.SetValue(AnimatedOffsetProperty,target);
                HistoryScroll.BeginAnimation(AnimatedOffsetProperty,null);
            };
            HistoryScroll.BeginAnimation(AnimatedOffsetProperty,animation,HandoffBehavior.SnapshotAndReplace);
        },DispatcherPriority.ContextIdle);
    }
    private Brush Brush(string key) => (Brush)FindResource(key);
    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if(sender is not Button {Tag:string name}) return;
        try { Clipboard.SetText(name); Notice?.Invoke("已复制昵称"); }
        catch(System.Runtime.InteropServices.ExternalException) { Notice?.Invoke("剪贴板暂时不可用，请重试。"); }
    }
    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button || button.Content is TextBlock) return;
        try
        {
            Clipboard.SetText(id);
            var original = button.Content; button.Content = new TextBlock { Text = "已复制", Foreground = Brush("Ink3") };
            await Task.Delay(1200); button.Content = original;
        }
        catch (System.Runtime.InteropServices.ExternalException) { Notice?.Invoke("剪贴板暂时不可用，请重试。"); }
    }
    private void Ranks_Click(object sender, RoutedEventArgs e)
    {
        if(sender is Button {Tag:PlayerIdentity player}) RanksRequested?.Invoke(player);
    }
    private void Player_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlayerIdentity player }) PlayerRequested?.Invoke(player);
    }
}
