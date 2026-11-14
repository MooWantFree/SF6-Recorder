using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Sf6Prototype;

public partial class RecentMatchesView : UserControl
{
    public const double HeaderHeight=48, ExpandedHeight=208;
    public static readonly DependencyProperty IsCollapsedProperty=DependencyProperty.Register(nameof(IsCollapsed),typeof(bool),typeof(RecentMatchesView),
        new PropertyMetadata(false,(d,_)=>((RecentMatchesView)d).CollapsedStateChanged()));
    public bool IsCollapsed {get=>(bool)GetValue(IsCollapsedProperty);set=>SetValue(IsCollapsedProperty,value);}
    public event Action<bool>? CollapsedChanged;
    private Archive? _archive;
    private DateTimeOffset _startedAt;
    private int? _characterId;
    private bool _updating;
    /// <summary>No ranked match has been played since launch; only the header is shown.</summary>
    public bool IsEmpty {get;private set;}=true;
    private bool? _shownEmpty;
    private Dictionary<string,Battle> _battles=[];
    public event Action<MatchListRow>? SelectionChanged;
    public RecentMatchesView() {InitializeComponent();UpdateCollapsedState();}
    private void CollapsedStateChanged() {UpdateCollapsedState();CollapsedChanged?.Invoke(IsCollapsed);}
    private void UpdateCollapsedState(bool animate=false)
    {
        if(CollapseButton==null) return;
        var compact=IsCollapsed || IsEmpty;
        MetricsPanel.Visibility=Chart.Visibility=ScoreMetric.Visibility=compact ? Visibility.Collapsed : Visibility.Visible;
        // Collapsing an empty panel would change nothing, so the empty header offers no toggle.
        CollapseButton.Visibility=IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility=IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        CharacterName.Visibility=IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.Content=IsCollapsed ? "\uE70D" : "\uE70E";
        var label=IsCollapsed ? Strings.ExpandRecentMatches : Strings.CollapseRecentMatches;
        CollapseButton.ToolTip=label;AutomationProperties.SetName(CollapseButton,label);
        ApplyHeight(compact ? HeaderHeight : ExpandedHeight,animate);
    }
    private void ApplyHeight(double target,bool animate)
    {
        // Follow-up reloads (images, scores) must not cut short a transition that is already heading here.
        if(!animate && Height==target) return;
        var from=ActualHeight;
        BeginAnimation(HeightProperty,null);MetricsPanel.BeginAnimation(OpacityProperty,null);Chart.BeginAnimation(OpacityProperty,null);
        Height=target;
        if(!animate || !IsVisible || !SystemParameters.ClientAreaAnimation || from<=0 || Math.Abs(from-target)<1) return;
        // The base value already holds the target; the animation only bridges from the old height.
        var duration=TimeSpan.FromMilliseconds(320);var ease=new CubicEase {EasingMode=EasingMode.EaseOut};
        BeginAnimation(HeightProperty,new DoubleAnimation(from,target,duration) {EasingFunction=ease,FillBehavior=FillBehavior.Stop});
        var reveal=new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(220)) {BeginTime=TimeSpan.FromMilliseconds(140),FillBehavior=FillBehavior.Stop};
        MetricsPanel.BeginAnimation(OpacityProperty,reveal);Chart.BeginAnimation(OpacityProperty,reveal);
    }
    private void Collapse_Click(object sender,RoutedEventArgs e)=>IsCollapsed=!IsCollapsed;
    public void Reload(Archive archive,DateTimeOffset startedAt)
    {
        _archive=archive;_startedAt=startedAt;
        var latest=archive.All.FirstOrDefault();
        if(latest?.CharacterId!=_characterId)
        {
            _updating=true;
            var ranked=archive.All.FirstOrDefault(b=>b.CharacterId==latest?.CharacterId && b.Category=="rank");
            ScoreMetric.SelectedIndex=ranked?.Mr>0 ? 1 : 0;
            _updating=false;_characterId=latest?.CharacterId;
        }
        Update();
    }
    private void Update()
    {
        if(_archive is not { } archive) return;
        var mr=ScoreMetric.SelectedIndex==1;
        var summary=RecentMatchScores.Build(archive.All,archive.Points,archive.Owner,_startedAt,mr);
        IsEmpty=summary.Matches.Count==0;
        // Grow only when the first match of this session arrives while the panel is on screen.
        UpdateCollapsedState(animate:_shownEmpty==true && !IsEmpty);
        _shownEmpty=IsEmpty;
        CharacterName.Text=summary.Latest is { } latest ? archive.Profiles.Names.Display(latest.CharacterId,latest.Character) : "";
        RecentTitle.ToolTip=$"{Strings.RecentMatchesScope}\n{_startedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        HighestScore.Text=summary.Highest?.ToString("N0") ?? "—";
        LowestScore.Text=summary.Lowest?.ToString("N0") ?? "—";
        var uncertain=summary.Pending || summary.Estimated;
        ScoreChange.Text=summary.Change is { } net ? net.ToString("+0;−0;0")+(uncertain ? "*" : "") : "—";
        ScoreChange.SetResourceReference(TextBlock.ForegroundProperty,summary.Change>0 ? "Win" : summary.Change<0 ? "Loss" : "Ink");
        ScoreChange.ToolTip=summary.Pending ? Strings.RecentScoresPending : summary.Estimated ? ScoreDisplay.EstimateExplanation : null;
        ScoreWinRate.Text=summary.WinRate is { } winRate ? $"{winRate:F1}%" : "—";
        ScoreWinRate.ToolTip=Localization.Format(Strings.MatchCount,summary.Matches.Count);
        _battles=summary.Matches.ToDictionary(b=>b.ReplayId);
        var chart=new List<ChartPoint>();var previousSettled=false;
        if(summary.InitialScore is { } initial && summary.Samples.FirstOrDefault() is { } first)
        {
            chart.Add(new("$session-start",first.Point.Time[..16],initial,false,true,"neutral",Strings.SessionBaseline));
            previousSettled=true;
        }
        foreach(var sample in summary.Samples)
        {
            var p=sample.Point;var b=sample.Battle;var delta=mr ? p.MrDelta : p.LpDelta;
            var change=delta is { } value ? value.ToString("+0;−0;0") : Strings.SettlementUnknown;
            var estimate=delta!=null && p.Estimated ? "\n"+ScoreDisplay.EstimateExplanation : "";
            chart.Add(new(b.ReplayId,p.Time[..16],sample.Value,sample.Settled && previousSettled,sample.Settled,Kinds.Of(b.Outcome),
                $"{Localization.Outcome(b.Outcome)} {b.Score}  ·  {change}  ·  {b.Opponent}{estimate}"));
            previousSettled=sample.Settled;
        }
        var selected=Chart.SelectedReplay;
        Chart.Unit=mr ? "MR" : "LP";
        Chart.EmptyText=summary.Matches.Count==0 ? Strings.NoRecentRankedMatches : mr ? Strings.NoMrForMatches : Strings.NoScores;
        Chart.Points=chart;
        Chart.SelectedReplay=chart.Any(p=>p.ReplayId==selected) ? selected : chart.LastOrDefault()?.ReplayId;
    }
    public void SelectReplay(string? replay)=>Chart.SelectedReplay=_battles.ContainsKey(replay ?? "") ? replay : null;
    private void Metric_Changed(object sender,SelectionChangedEventArgs e) {if(!_updating)Update();}
    private void Chart_PointInvoked(object? sender,ChartPoint point)
    {
        if(_archive!=null && _battles.TryGetValue(point.ReplayId,out var battle)) SelectionChanged?.Invoke(_archive.Row(battle));
    }
}
