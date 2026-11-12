using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Sf6Prototype;

/// <summary>One match. Settled points show the post-match score; unsettled ones the recorded pre-match score.</summary>
public record ChartPoint(string ReplayId, string Time, double Value, bool Connect, bool Settled, string Kind, string Caption);

public sealed class ScoreChart : FrameworkElement
{
    public event EventHandler<ChartPoint>? PointInvoked;
    private IReadOnlyList<ChartPoint> _points = [];
    private string? _selectedReplay;
    private readonly List<Point> _locations = [];
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Mouse, StaysOpen = true };
    private int _hover = -1;
    public IReadOnlyList<ChartPoint> Points { get => _points; set { _points = value; _locations.Clear(); _selectedReplay = null; ResetHover(); } }
    public string? SelectedReplay { get => _selectedReplay; set { _selectedReplay = value; InvalidateVisual(); } }
    public string Unit { get; set; } = "LP";
    public string EmptyText { get; set; } = "";
    public ScoreChart()
    {
        Focusable = true; FocusVisualStyle = null;
        MouseLeave += (_, _) => ResetHover();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ResetHover(); };
        _tip.Opened += (_, _) => UiCapture.SaveLater(_tip, "ui-score-tooltip.png");
    }
    private void ResetHover() { _hover = -1; _tip.IsOpen = false; Cursor = Cursors.Arrow; InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
    private int HitPoint(Point cursor)
    {
        var found = -1; var distance = 14d * 14;
        for (var i = 0; i < _locations.Count; i++) { var d = (_locations[i] - cursor).LengthSquared; if (d < distance) { distance = d; found = i; } }
        return found;
    }
    private void Invoke(int index)
    {
        if (index < 0 || index >= _points.Count) return;
        SelectedReplay = _points[index].ReplayId; PointInvoked?.Invoke(this, _points[index]);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var index = HitPoint(e.GetPosition(this)); Focus();
        if (index >= 0) { Invoke(index); e.Handled = true; }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_points.Count == 0) return;
        var index = _points.ToList().FindIndex(p => p.ReplayId == SelectedReplay);
        if (e.Key is Key.Left or Key.Right)
        {
            index = index < 0 ? _points.Count - 1 : Math.Clamp(index + (e.Key == Key.Right ? 1 : -1), 0, _points.Count - 1);
            Invoke(index); e.Handled = true;
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var found = HitPoint(e.GetPosition(this));
        Cursor = found >= 0 ? Cursors.Hand : Cursors.Arrow;
        if (found == _hover) return;
        _hover = found; _tip.IsOpen = false;
        if (found >= 0)
        {
            var p = _points[found];
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = p.Time, FontSize = 11, Foreground = Res("Ink3") });
            content.Children.Add(new TextBlock { Text = $"{p.Value:N0} {Unit}", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new(0, 4, 0, 3) });
            content.Children.Add(new TextBlock { Text = p.Caption, FontSize = 12, Foreground = Res(p.Kind switch { "win" => "Win", "loss" => "Loss", _ => "Ink2" }) });
            _tip.Content = content; _tip.PlacementTarget = this; _tip.IsOpen = true;
        }
        InvalidateVisual();
    }
    private static (double Low, double High, double Step) NiceScale(double min, double max)
    {
        if (max - min < 20) { var mid = (max + min) / 2; min = mid - 10; max = mid + 10; }
        var rough = (max - min) / 4; var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough))); var n = rough / magnitude;
        var step = Math.Max(1, (n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10) * magnitude);
        var low = Math.Floor(min / step) * step; var high = Math.Ceiling(max / step) * step;
        return (low, high == low ? low + step : high, step);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); _locations.Clear();
        var width = ActualWidth; var height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (width < 150 || height < 100) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string text, Brush color) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, color, dpi);
        if (_points.Count == 0)
        {
            var empty = new FormattedText(EmptyText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface((FontFamily)FindResource("UiFont"),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal), 13, Res("Ink3"), dpi);
            dc.DrawText(empty, new Point((width - empty.Width) / 2, (height - empty.Height) / 2)); return;
        }
        var (low, high, step) = NiceScale(_points.Min(p => p.Value), _points.Max(p => p.Value));
        const double left = 60, top = 28; var right = width - 12; var bottom = height - 48;
        var grid = new Pen(Res("LineSubtle"), 1); var ink3 = Res("Ink3");
        for (var v = low; v <= high + step / 2; v += step)
        {
            var y = Math.Round(bottom - (bottom - top) * (v - low) / (high - low)) + .5;
            dc.DrawLine(grid, new(left, y), new(right, y));
            var label = Text(v.ToString("N0"), ink3); dc.DrawText(label, new Point(left - 10 - label.Width, y - label.Height / 2));
        }
        double X(int i) => _points.Count == 1 ? (left + right) / 2 : left + 14 + (right - left - 28) * i / (_points.Count - 1);
        for (var i = 0; i < _points.Count; i++) _locations.Add(new(X(i), bottom - (bottom - top) * (_points[i].Value - low) / (high - low)));
        // Fill each connected run separately; missing settlements/season boundaries stay disconnected.
        var fill=new StreamGeometry();
        using(var geometry=fill.Open())
        {
            var start=0;
            for(var end=1;end<=_points.Count;end++)
            {
                if(end<_points.Count && _points[end].Connect) continue;
                if(end-start>1)
                {
                    geometry.BeginFigure(new(_locations[start].X,bottom),true,true);
                    for(var j=start;j<end;j++) geometry.LineTo(_locations[j],true,false);
                    geometry.LineTo(new(_locations[end-1].X,bottom),true,false);
                }
                start=end;
            }
        }
        fill.Freeze(); dc.DrawGeometry(Res("ChartFill"),null,fill);
        var line = new Pen(Res("Accent"), 2) { LineJoin = PenLineJoin.Round };
        for (var i = 1; i < _points.Count; i++) if (_points[i].Connect) dc.DrawLine(line, _locations[i - 1], _locations[i]);
        var halo = Res("ChartHalo"); var ring = new Pen(Res("Accent"), 1.5);
        var radius = _points.Count > 60 ? 1.75 : 2.5;
        for (var i = 0; i < _points.Count; i++)
        {
            var p = _points[i]; var at = _locations[i]; var selected = p.ReplayId == _selectedReplay;
            var active=i==_hover || selected;
            var color = active ? Res(p.Kind switch { "win" => "Win", "loss" => "Loss", _ => "Neutral" }) : Res("Accent");
            if (i == _hover || selected) dc.DrawEllipse(halo, null, at, 11, 11);
            if (selected) dc.DrawEllipse(null, ring, at, 7, 7);
            var pointRadius=p.Settled ? (active ? 4.5 : radius) : (active ? 5 : 4);
            dc.DrawEllipse(p.Settled ? color : Res("Surface"), new Pen(color, p.Settled ? 1 : 1.5), at, pointRadius, pointRadius);
        }
        var extremes=DateRanges.Extrema(_points.Select(p=>p.Value).ToArray());
        for(var n=0;n<extremes.Count;n++)
        {
            var i=extremes[n];var point=_locations[i];var label=Text(_points[i].Value.ToString("N0"),Res("AccentText"));
            label.SetFontWeight(FontWeights.SemiBold);
            var x=Math.Clamp(point.X-label.Width/2,left,right-label.Width);
            var y=n==0 ? point.Y-label.Height-9 : point.Y+9;
            dc.DrawRoundedRectangle(Res("Surface"),null,new Rect(x-3,y-1,label.Width+6,label.Height+2),3,3);
            dc.DrawText(label,new Point(x,y));
        }
        string? lastDate = null; var lastX = double.NegativeInfinity;
        for (var i = 0; i < _points.Count; i++)
        {
            var date = _points[i].Time.Substring(5, 5).Replace('-', '/');
            if (date == lastDate || _locations[i].X - lastX < 48) continue;
            var label = Text(date, ink3); dc.DrawText(label, new Point(Math.Clamp(_locations[i].X - label.Width / 2, left, right - label.Width), height - 16));
            lastDate = date; lastX = _locations[i].X;
        }
    }
}
