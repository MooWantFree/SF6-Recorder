using System.Windows;
using System.Windows.Controls;

namespace Sf6Prototype;

public partial class OverlayMatchCard : UserControl
{
    private OverlayStyle _style = new();
    private RecentMatchDisplay? _session;

    public OverlayMatchCard() => InitializeComponent();

    public void ApplyAppearance(ConnectionSettings settings)
    {
        var green = settings.OverlayGreenScreen;
        Backdrop.SetResourceReference(Border.BackgroundProperty,green ? "StreamChromaGreen" : "Surface");
        Backdrop.CornerRadius = new CornerRadius(RecentMatchesStreamWindow.NeedsTransparency(settings) ? 8 : 0);
        Backdrop.Opacity = green ? 1 : settings.OverlayBackgroundOpacity / 100d;
        if (_style == settings.OverlayStyle) return;
        _style = settings.OverlayStyle;
        ApplyTrendLayout();
        if (_session != null) RenderSession();
    }

    private void ApplyTrendLayout()
    {
        var beside = _style.ShowTrend && !_style.TrendBelow;
        var below = _style.ShowTrend && _style.TrendBelow;
        var star = new GridLength(1, GridUnitType.Star);
        SummaryColumn.Width = beside ? GridLength.Auto : star;
        TrendColumn.Width = beside ? star : new GridLength(0);
        TrendRow.Height = below ? star : new GridLength(0);
        Grid.SetColumn(TrendPanel, beside ? 1 : 0);
        Grid.SetRow(TrendPanel, below ? 2 : 0);
        SummaryBar.Margin = new Thickness(12, 5, beside ? 0 : 12, below ? 0 : 5);
        TrendPanel.Margin = beside ? new Thickness(_style.TrendSpacing, 5, 0, 5) : new Thickness(12, 0, 12, 4);
        TrendPanel.BorderThickness = beside ? new Thickness(1, 0, 0, 0) : new Thickness(0, 1, 0, 0);
        TrendPanel.Visibility = _style.ShowTrend ? Visibility.Visible : Visibility.Collapsed;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size available)
    {
        // Size the left column to the rendered numbers, while reserving space for the curve.
        SummaryBar.MaxWidth = _style.ShowTrend && !_style.TrendBelow
            ? Math.Max(0, available.Width * 2 / 3 - SummaryBar.Margin.Left) : double.PositiveInfinity;
        GapRow.Height = new GridLength(_style.ShowTrend && _style.TrendBelow
            ? _style.TrendSpacing * Math.Min(1, available.Height / 160) : 0);
        return base.MeasureOverride(available);
    }

    public void Display(RecentMatchDisplay session)
    {
        _session = session;
        RenderSession();
    }

    private void RenderSession()
    {
        var session = _session!;
        ScoreLabel.SetResourceReference(TextBlock.TextProperty, session.Unit switch {
            "LP" => "I18n.OverlayCurrentLP", "MR" => "I18n.OverlayCurrentMR", _ => "I18n.OverlayCurrentScore" });
        DataContext = session;
        SummaryBar.HorizontalAlignment = _style.ShowCurrent && _style.ShowChange && _style.ShowCount && _style.ShowWinRate
            ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        CurrentMetric.Visibility = _style.ShowCurrent ? Visibility.Visible : Visibility.Collapsed;
        ChangeMetric.Visibility = _style.ShowChange ? Visibility.Visible : Visibility.Collapsed;
        CountMetric.Visibility = _style.ShowCount ? Visibility.Visible : Visibility.Collapsed;
        WinRateMetric.Visibility = _style.ShowWinRate ? Visibility.Visible : Visibility.Collapsed;
        ChangeValue.SetResourceReference(TextBlock.ForegroundProperty, session.ChangeBrush);
        if (!_style.ShowTrend) return;

        var trend = session.WithLimit(_style.MatchLimit);
        Plot.OverlayStyle = _style;
        Plot.Unit = trend.Unit;
        Plot.EmptyText = trend.EmptyText;
        Plot.Points = trend.Points;
        Plot.SelectedReplay = trend.Points.LastOrDefault()?.ReplayId;
    }
}
