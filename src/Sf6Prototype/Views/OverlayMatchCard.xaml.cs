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
        if (_session != null) RenderSession();
    }

    public void Display(RecentMatchDisplay session)
    {
        _session = session;
        RenderSession();
    }

    private void RenderSession()
    {
        var display = _session!.WithLimit(_style.MatchLimit);
        DataContext = display;
        var extremes = _style.ShowExtremes;
        HighestMetric.Visibility = LowestMetric.Visibility = extremes ? Visibility.Visible : Visibility.Collapsed;
        ChangeMetric.Visibility = _style.ShowChange ? Visibility.Visible : Visibility.Collapsed;
        WinRateMetric.Visibility = _style.ShowWinRate ? Visibility.Visible : Visibility.Collapsed;
        var visibleMetrics = Metrics.Children.Cast<UIElement>()
            .Where(metric => metric.Visibility == Visibility.Visible).ToArray();
        Metrics.Visibility = !display.IsEmpty && visibleMetrics.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Each metric spans two half-columns. Split the unused columns equally between the ends.
        var firstColumn = Metrics.ColumnDefinitions.Count / 2 - visibleMetrics.Length;
        for (var i = 0; i < visibleMetrics.Length; i++)
            Grid.SetColumn(visibleMetrics[i], firstColumn + 2 * i);
        ScoreHeader.Text = _style.ShowCurrent && !display.IsEmpty ? $"{display.CurrentScore} {display.Unit}" : display.Unit;
        ChangeValue.SetResourceReference(TextBlock.ForegroundProperty,display.ChangeBrush);
        Plot.OverlayStyle = _style;
        Plot.Unit = display.Unit;
        Plot.EmptyText = display.EmptyText;
        Plot.Points = display.Points;
        Plot.SelectedReplay = display.Points.LastOrDefault()?.ReplayId;
    }

    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var scale = Math.Clamp(Math.Min((e.NewSize.Width - 32) / 388, (e.NewSize.Height - 16) / 198), 0.5, 1);
        ContentScale.ScaleX = ContentScale.ScaleY = scale;
    }
}
