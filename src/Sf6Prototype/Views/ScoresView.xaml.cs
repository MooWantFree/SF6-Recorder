using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Sf6Prototype;

public partial class ScoresView : UserControl
{
    private Archive? _archive;
    private bool _ready, _loading;
    private Dictionary<string, Battle> _battles = [];
    public event Action<MatchListRow?>? SelectionChanged;
    public event Action? DataRequested;
    public MatchListRow? Selected { get; private set; }
    private int SelectedCount => int.TryParse((ScoreCount.SelectedItem as ComboBoxItem)?.Tag?.ToString(),out var n) ? n : 20;
    public ScoreRange Range => new((CharacterList.SelectedItem as CharacterCard)?.Id,SelectedCount,DateRanges.Epoch(ScoreDates.Start),DateRanges.Epoch(ScoreDates.End?.AddDays(1)));
    public ScoresViewState CaptureState() => new(Range.CharacterId,ScoreMetric.SelectedIndex==1,SelectedCount,Selected?.Battle.ReplayId,ScoreDates.Start,ScoreDates.End);
    public void SetLoading(bool loading) { _loading=loading; UpdateLoading(); }
    private void UpdateLoading()
    {
        LoadingState.Visibility=_loading ? Visibility.Visible : Visibility.Collapsed;
        Chart.Opacity=_loading ? 0.4 : 1;
        Chart.Visibility=_loading && Chart.Points.Count==0 ? Visibility.Hidden : Visibility.Visible;
    }
    public ScoresView() => InitializeComponent();
    public void Bind(Archive archive) => _archive = archive;
    public void ResetSelection()
    {
        _ready = false; Selected = null; CharacterList.SelectedIndex = -1;
        ScoreCount.SelectedIndex = ScoreMetric.SelectedIndex = 0;
        ScoreDates.SetRange(null,null);
    }
    public void Reload(ScoresViewState? restore=null)
    {
        if (_archive is not { } a) return;
        _ready = false;
        var state=restore ?? CaptureState();
        if(restore!=null) Selected=null;
        var cards = a.All.Where(b => b.Category == "rank").GroupBy(b => b.CharacterId).OrderByDescending(g => g.Count()).Select(g => Card(a, g.ToList())).ToList();
        CharacterList.ItemsSource = cards;
        CharacterList.SelectedItem = cards.FirstOrDefault(c => c.Id == state.CharacterId) ?? cards.FirstOrDefault();
        ScoreMetric.SelectedIndex=state.ShowMr ? 1 : 0;
        ScoreCount.SelectedItem=ScoreCount.Items.OfType<ComboBoxItem>().FirstOrDefault(i=>i.Tag?.ToString()==state.Count.ToString()) ?? ScoreCount.Items[0];
        ScoreDates.SetRange(state.Start,state.End);
        _ready = true; Update(state.SelectedReplay);
        if(restore!=null && CharacterList.SelectedItem!=null) CharacterList.ScrollIntoView(CharacterList.SelectedItem);
    }
    private static CharacterCard Card(Archive a, List<Battle> games)
    {
        var last = games.MaxBy(b => (b.PlayedAt, b.ReplayId))!;
        var point = a.Points.GetValueOrDefault(last.ReplayId);
        var lp = point?.LpAfter ?? last.Lp;
        var rating = last.Mr > 0 && (point?.MrAfter ?? last.Mr) is { } mr ? $"{mr:N0} MR" : ScoreDisplay.WithUnit(lp,"LP");
        var now = a.Current?.Characters.FirstOrDefault(c => c.CharacterId == last.CharacterId && c.Ranked);
        var rank = now ?? new CharacterRank(last.CharacterId, last.Character, lp, last.Mr, last.LeagueRank, last.MasterLeague,last.MasterRatingRanking);
        var wins = games.Count(b => b.Outcome == "胜"); var decided = games.Count(b => b.Outcome is "胜" or "负" or "平");
        var detail = string.Join(" · ", new[] { $"{games.Count} 场", decided == 0 ? null : $"胜率 {100d * wins / decided:F0}%" }.OfType<string>());
        return new(last.CharacterId, last.Character, a.Image(last.CharacterId, last.Character), rating, detail, rank);
    }
    private void Update(string? selectedReplay=null)
    {
        if (!_ready || _archive is not { } a) return;
        var card = CharacterList.SelectedItem as CharacterCard;
        var showMr = ScoreMetric.SelectedIndex == 1;
        var count = SelectedCount;
        _battles = card == null ? [] : a.All.Where(b => b.Category == "rank" && b.CharacterId == card.Id).ToDictionary(b => b.ReplayId);
        var range=Range;
        var recent = DateRanges.Recent(a.Points.Values.Where(p => _battles.ContainsKey(p.ReplayId) && range.Contains(p.PlayedAt)), count);

        var chart = new List<ChartPoint>(); var previousSettled = false;
        foreach (var p in recent)
        {
            var b = _battles[p.ReplayId];
            var settled = (showMr ? p.MrAfter : p.LpAfter) != null;
            double? value = showMr ? p.MrAfter ?? (p.Mr > 0 ? p.Mr : null) : p.LpAfter ?? p.Lp;
            if (value == null || value<0) { previousSettled = false; continue; }
            var delta = showMr ? p.MrDelta : p.LpDelta;
            var change = delta == null ? "本场结算未知" : $"{delta:+0;−0;0}";
            var estimate = delta!=null && p.Estimated ? "\n"+ScoreDisplay.EstimateExplanation : "";
            chart.Add(new(p.ReplayId, p.Time[..16], value.Value, settled && previousSettled, settled, Kinds.Of(b.Outcome), $"{b.Outcome} {b.Score}  ·  {change}  ·  {b.Opponent}{estimate}"));
            previousSettled = settled;
        }
        var last = recent.LastOrDefault();
        var current=showMr ? last?.MrAfter ?? (last?.Mr > 0 ? last.Mr : null) : last?.LpAfter ?? last?.Lp;
        CurrentScore.Text = ScoreDisplay.Value(current);
        CurrentScore.ToolTip = last?.Estimated==true && (showMr ? last.MrDelta : last.LpDelta)!=null ? ScoreDisplay.EstimateExplanation : null;
        CurrentScore.FontSize=current<0 ? 18 : 26;
        ScoreUnit.Text = current<0 ? "" : showMr ? "MR" : "LP";
        var difference = DateRanges.ScoreDifference(recent, showMr);
        ScoreChange.Text = difference?.ToString("+0;−0;0") ?? "—";
        ScoreChange.Foreground = (Brush)FindResource(difference > 0 ? "Win" : difference < 0 ? "Loss" : "Ink");
        var games = recent.Select(p => _battles[p.ReplayId]).ToList();
        var decided = games.Count(b => b.Outcome is "胜" or "负" or "平");
        ScoreWinRate.Text = decided == 0 ? "—" : $"{100d * games.Count(b => b.Outcome == "胜") / decided:F1}%";

        var keep = selectedReplay ?? Selected?.Battle.ReplayId;
        Chart.Unit = showMr ? "MR" : "LP";
        Chart.EmptyText = card == null ? "暂无排位记录" : showMr ? "这些对局没有 MR" : "暂无可显示的分数";
        Chart.Points = chart;
        Select(chart.FirstOrDefault(p => p.ReplayId == keep) ?? chart.LastOrDefault());
        UpdateLoading();
    }
    private void Select(ChartPoint? point)
    {
        Chart.SelectedReplay = point?.ReplayId;
        Selected = point != null && _battles.TryGetValue(point.ReplayId, out var b) ? _archive!.Row(b) : null;
        SelectionChanged?.Invoke(Selected);
    }
    private void Chart_PointInvoked(object? sender, ChartPoint e) { Select(e); }
    private void Character_Changed(object sender, SelectionChangedEventArgs e) { if (!_ready) return; Selected = null; Update(); DataRequested?.Invoke(); }
    private void Option_Changed(object sender, SelectionChangedEventArgs e) { if(!_ready) return; Update(); DataRequested?.Invoke(); }
    private void Date_Changed(object? sender,EventArgs e) {if(!_ready) return;Update();DataRequested?.Invoke();}
}
