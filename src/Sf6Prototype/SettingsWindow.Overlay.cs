using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Sf6Prototype;

public partial class SettingsWindow
{
    private bool _loadingStyle;
    private int _lastValidLimit = OverlayStyle.MaxMatches;
    private OverlayStyle _draftStyle = new();
    private readonly CharacterNames _sampleNames = new(App.DataDirectory);
    private static readonly RecentMatchSummary SampleSession = CreateSampleSession();
    private RecentMatchDisplay? _sampleDisplay;
    private bool _previewOnStream;
    internal RecentMatchDisplay? StreamSample => _previewOnStream ? _sampleDisplay : null;
    internal event Action? StreamSampleChanged;

    private void ToggleStreamPreview(object sender, RoutedEventArgs e) => SetStreamPreview(!_previewOnStream);

    internal void SetStreamPreview(bool enabled)
    {
        if (_previewOnStream == enabled) return;
        _previewOnStream = enabled;
        PreviewOnStream.SetResourceReference(ContentControl.ContentProperty,
            enabled ? "I18n.OverlayEndPreview" : "I18n.OverlayPreviewOnStream");
        StreamSampleChanged?.Invoke();
    }

    private static RecentMatchSummary CreateSampleSession()
    {
        int[] deltas = [50,-40,-40,55,-40,-40,-40,50,55,60,-40,55,-40,60,55,-40,50,55,-40,60,50,-40,-40,55,-40,-40,55,45,-40,-40];
        var matches = new List<Battle>();
        var points = new Dictionary<string,ScorePoint>();
        var start = new DateTimeOffset(2025,1,1,12,0,0,TimeSpan.Zero);
        var score = 18200;
        foreach (var delta in deltas)
        {
            var battle = new Battle { OwnerId="sample", ReplayId=$"sample-{matches.Count}", CharacterId=1,
                Category="rank", PlayedAt=start.AddMinutes(3*matches.Count).ToUnixTimeSeconds(),
                Lp=score, MyRounds=delta > 0 ? 2 : 1, OpponentRounds=delta > 0 ? 1 : 2 };
            matches.Add(battle);
            points.Add(battle.ReplayId,new(battle.PlayedAt,battle.Time,score,null,delta,null,battle.ReplayId));
            score += delta;
        }
        return RecentMatchScores.Build(matches,points,"sample",start,false);
    }

    private void DisplaySample()
    {
        _sampleDisplay = RecentMatchDisplay.From(SampleSession,_sampleNames.Display(1,"Ryu"),false);
        SampleCard.Display(_sampleDisplay);
        if (StreamSample != null) StreamSampleChanged?.Invoke();
    }

    private void InitializeOverlay(ConnectionSettings current)
    {
        foreach (var picker in new[] { WinColor,LossColor,LineColor })
        {
            foreach (var color in Enum.GetValues<OverlayColor>())
            {
                var swatch = new Rectangle { Width=13, Height=13, RadiusX=3, RadiusY=3, Margin=new Thickness(0,0,6,0) };
                swatch.SetResourceReference(Shape.FillProperty,"Overlay" + color);
                swatch.SetResourceReference(Shape.StrokeProperty,"Line");
                var name = new TextBlock();
                name.SetResourceReference(TextBlock.TextProperty,"I18n.Overlay" + color);
                var item = new ComboBoxItem { Tag=color, Content=new StackPanel {
                    Orientation=Orientation.Horizontal, Children={ swatch,name } } };
                item.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty,"I18n.Overlay" + color);
                picker.Items.Add(item);
            }
        }
        OverlayStartup.IsChecked = current.OpenOverlayOnStartup;
        OverlayTopmost.IsChecked = current.OverlayAlwaysOnTop;
        DisplaySample();
        LoadStyle(current);
        foreach (var check in new[] { OverlayGreen,OverlayTopmost,OverlayPulse,OverlayCurrent,OverlayExtremes,OverlayWinRate,OverlayChange })
        {
            check.Checked += (_,_) => PreviewOverlay();
            check.Unchecked += (_,_) => PreviewOverlay();
        }
        foreach (var picker in new[] { WinColor,LossColor,LineColor }) picker.SelectionChanged += (_,_) => PreviewOverlay();
        OverlayLimit.TextChanged += (_,_) => PreviewOverlay();
        OverlayOpacity.Changed += PreviewOverlay;
    }

    private void LoadStyle(ConnectionSettings settings)
    {
        _loadingStyle = true;
        var style = settings.OverlayStyle;
        OverlayCurrent.IsChecked = style.ShowCurrent;
        OverlayExtremes.IsChecked = style.ShowExtremes;
        OverlayChange.IsChecked = style.ShowChange;
        OverlayWinRate.IsChecked = style.ShowWinRate;
        OverlayPulse.IsChecked = style.HighlightNewMatch;
        OverlayLimit.Text = style.MatchLimit.ToString(CultureInfo.InvariantCulture);
        WinColor.SelectedValue = style.WinColor;
        LossColor.SelectedValue = style.LossColor;
        LineColor.SelectedValue = style.LineColor;
        OverlayGreen.IsChecked = settings.OverlayGreenScreen;
        OverlayOpacity.Value = settings.OverlayBackgroundOpacity;
        _loadingStyle = false;
        PreviewOverlay();
    }

    private bool TryOverlayLimit(out int limit) => int.TryParse(OverlayLimit.Text,out limit) &&
        limit is >= OverlayStyle.MinMatches and <= OverlayStyle.MaxMatches;

    private bool ValidOverlayLimit() => TryOverlayLimit(out _);

    private void CheckOverlayLimit(object sender, RoutedEventArgs e) =>
        MatchLimitError.Visibility = ValidOverlayLimit() ? Visibility.Collapsed : Visibility.Visible;

    private void ResetOverlayStyle(object sender,RoutedEventArgs e) => LoadStyle(new());

    private void PreviewOverlay()
    {
        if (_loadingStyle) return;
        if (TryOverlayLimit(out var limit))
        {
            _lastValidLimit = limit;
            MatchLimitError.Visibility = Visibility.Collapsed;
        }
        OverlayOpacity.GreenScreen = OverlayGreen.IsChecked == true;
        _draftStyle = new() { ShowCurrent=OverlayCurrent.IsChecked == true, ShowExtremes=OverlayExtremes.IsChecked == true,
            ShowChange=OverlayChange.IsChecked == true, ShowWinRate=OverlayWinRate.IsChecked == true,
            HighlightNewMatch=OverlayPulse.IsChecked == true, MatchLimit=_lastValidLimit,
            WinColor=(OverlayColor)WinColor.SelectedValue, LossColor=(OverlayColor)LossColor.SelectedValue,
            LineColor=(OverlayColor)LineColor.SelectedValue };
        SampleCard.ApplyAppearance(OverlayAppearance(_current));
        OverlayPreviewChanged?.Invoke();
    }

    internal ConnectionSettings OverlayAppearance(ConnectionSettings settings) => settings with {
        OverlayAlwaysOnTop=OverlayTopmost.IsChecked == true, OverlayGreenScreen=OverlayGreen.IsChecked == true,
        OverlayBackgroundOpacity=OverlayOpacity.Value, OverlayStyle=_draftStyle };
}
