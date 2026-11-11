using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Sf6Prototype;

public sealed class ScoreChart : FrameworkElement
{
    public IReadOnlyList<ScorePoint> Points { get; set; } = [];
    public bool ShowMr { get; set; }
    private readonly List<(Point Location, ScorePoint Record, double Value)> _markers = [];
    private readonly ToolTip _tip = new() { Placement = PlacementMode.MousePoint, StaysOpen = true, Padding = new(12,9,12,9), Background = Brushes.White, Foreground = Brushes.Black };
    private int _hover = -1;
    public ScoreChart()
    {
        MouseLeave += (_,_) => { _tip.IsOpen = false; _hover = -1; InvalidateVisual(); };
        _tip.Opened += (_,_) => UiCapture.SaveLater(_tip,"ui-score-tooltip.png");
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var cursor = e.GetPosition(this);
        var found = _markers.FindIndex(p => (p.Location-cursor).Length <= 18);
        if (found == _hover) return;
        _hover = found; _tip.IsOpen = false;
        if (found >= 0)
        {
            var p = _markers[found]; var delta = ShowMr ? p.Record.MrChange : p.Record.LpChange;
            _tip.Content = $"{p.Record.Time}\n{(ShowMr ? "MR" : "LP")}  {p.Value:N0}    差值 {delta}\n回放 {p.Record.ReplayId}";
            _tip.PlacementTarget = this; _tip.IsOpen = true;
        }
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); _markers.Clear();
        var width = ActualWidth; var height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,width,height));
        if (width < 150 || height < 100) return;
        void Text(string text, double x, double y, Brush? color = null) => dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),11,color ?? Brushes.Gray,VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(x,y));
        var values = Points.Where(p => ShowMr ? p.Mr > 0 : p.Lp != null).Select(p => (Point:p,Value:(double)(ShowMr ? p.Mr!.Value : p.Lp!.Value))).ToList();
        if (values.Count == 0) { _tip.IsOpen = false; Text(ShowMr ? "这些记录没有有效的 MR" : "选择角色以查看分数",32,height/2); return; }
        var min = values.Min(x => x.Value); var max = values.Max(x => x.Value);
        var pad = Math.Max(15,(max-min)*.18); min -= pad; max += pad;
        const double left = 64, top = 20; var right = width-30; var bottom = height-42;
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(235,235,240)),1);
        for (var i=0; i<=4; i++)
        {
            var y = top+(bottom-top)*i/4; dc.DrawLine(grid,new(left,y),new(right,y)); Text((max-(max-min)*i/4).ToString("N0"),4,y-8);
        }
        var line = new SolidColorBrush(Color.FromRgb(99,99,174)); var pen = new Pen(line,1.6);
        for (var i=0; i<values.Count; i++)
        {
            // Equal spacing represents match order, not elapsed time.
            var x = values.Count==1 ? (left+right)/2 : left+(right-left)*i/(values.Count-1);
            var y = bottom-(bottom-top)*(values[i].Value-min)/(max-min); var location = new Point(x,y);
            if (i>0 && (!ShowMr || values[i].Point.MrDelta != null)) dc.DrawLine(pen,_markers[^1].Location,location);
            _markers.Add((location,values[i].Point,values[i].Value));
        }
        for (var i=0; i<_markers.Count; i++)
        {
            var point = _markers[i].Location;
            if (i==_hover) dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(35,91,91,214)),null,point,10,10);
            dc.DrawEllipse(Brushes.White,pen,point,i==_hover ? 5 : 3.5,i==_hover ? 5 : 3.5);
        }
        var stride = Math.Max(1,(int)Math.Ceiling(values.Count/8d));
        for (var i=0; i<values.Count; i++) if (i%stride==0 || i==values.Count-1) Text((i+1).ToString(),_markers[i].Location.X-4,bottom+11);
        Text("对局顺序 →",Math.Max(left,right-70),bottom+25);
    }
}

