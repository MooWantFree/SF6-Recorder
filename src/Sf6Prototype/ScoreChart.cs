using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Sf6Prototype;

public sealed class ScorePointInvokedEventArgs(ScorePoint record, Point position) : EventArgs
{
    public ScorePoint Record { get; } = record;
    public Point Position { get; } = position;
}

public sealed class ScoreChart : FrameworkElement
{
    public event EventHandler<ScorePointInvokedEventArgs>? PointInvoked;
    private string? _selectedReplay;
    public string? SelectedReplay { get => _selectedReplay; set { _selectedReplay=value; _tip.IsOpen=false; InvalidateVisual(); } }
    private IReadOnlyList<ScorePoint> _points = [];
    private bool _showMr;
    public IReadOnlyList<ScorePoint> Points { get => _points; set { _points=value; ResetHover(); } }
    public bool ShowMr { get => _showMr; set { _showMr=value; ResetHover(); } }
    private readonly List<(Point Location, ScorePoint Record, double Value)> _markers = [];
    private readonly ToolTip _tip = new() { Placement = PlacementMode.MousePoint, StaysOpen = true, Padding = new(12,9,12,9), Background = Brushes.White, Foreground = Brushes.Black };
    private int _hover = -1;
    public ScoreChart()
    {
        Focusable = true;
        MouseLeave += (_,_) => { _tip.IsOpen = false; _hover = -1; Cursor=Cursors.Arrow; InvalidateVisual(); };
        _tip.Opened += (_,_) => UiCapture.SaveLater(_tip,"ui-score-tooltip.png");
        IsVisibleChanged += (_,_) => { if(!IsVisible) ResetHover(); };
    }
    private void ResetHover() { _hover=-1; _selectedReplay=null; _markers.Clear(); _tip.IsOpen=false; Cursor=Cursors.Arrow; InvalidateVisual(); }
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    private int HitPoint(Point cursor)
    {
        var found = -1; var distance = 18d*18;
        for(var i=0;i<_markers.Count;i++) { var d=(_markers[i].Location-cursor).LengthSquared; if(d<distance){distance=d;found=i;} }
        return found;
    }
    private void InvokePoint(int index)
    {
        if(index<0 || index>=_markers.Count) return;
        var marker=_markers[index]; SelectedReplay=marker.Record.ReplayId;
        PointInvoked?.Invoke(this,new(marker.Record,marker.Location));
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var index=HitPoint(e.GetPosition(this));
        if(index<0) return;
        Focus(); InvokePoint(index); e.Handled=true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if(_markers.Count==0) return;
        var index=_markers.FindIndex(p=>p.Record.ReplayId==SelectedReplay);
        if(e.Key is Key.Left or Key.Right)
        {
            index=index<0 ? (e.Key==Key.Right ? 0 : _markers.Count-1) : Math.Clamp(index+(e.Key==Key.Right ? 1 : -1),0,_markers.Count-1);
            InvokePoint(index); e.Handled=true;
        }
        else if(e.Key is Key.Enter or Key.Space) { InvokePoint(index<0 ? _markers.Count-1 : index); e.Handled=true; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var found = HitPoint(e.GetPosition(this));
        Cursor=found>=0 ? Cursors.Hand : Cursors.Arrow;
        if (found == _hover) return;
        _hover = found; _tip.IsOpen = false;
        if (found >= 0 && SelectedReplay==null)
        {
            var p = _markers[found]; var delta = ShowMr ? p.Record.MrChange : p.Record.LpChange;
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text=p.Record.Time,FontSize=11,Foreground=Brushes.Gray });
            content.Children.Add(new TextBlock { Text=$"{p.Value:N0}  {(ShowMr ? "MR" : "LP")}",FontSize=17,FontWeight=FontWeights.SemiBold,Margin=new(0,6,0,5) });
            content.Children.Add(new TextBlock { Text=$"记录变化  {delta}",FontSize=12,Foreground=new SolidColorBrush(Color.FromRgb(105,105,125)) });
            _tip.Content = content;
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
            var selected=_markers[i].Record.ReplayId==SelectedReplay;
            if (i==_hover || selected) dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(35,91,91,214)),null,point,10,10);
            dc.DrawEllipse(selected ? line : Brushes.White,pen,point,i==_hover || selected ? 5 : 3.5,i==_hover || selected ? 5 : 3.5);
        }
        var stride = Math.Max(1,(int)Math.Ceiling(values.Count/8d));
        for (var i=0; i<values.Count; i++) if (i%stride==0 || i==values.Count-1) Text((i+1).ToString(),_markers[i].Location.X-4,bottom+11);
        Text("对局顺序 →",Math.Max(left,right-70),bottom+25);
    }
}

