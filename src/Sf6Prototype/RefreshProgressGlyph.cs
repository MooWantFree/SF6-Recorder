using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Sf6Prototype;

public sealed class RefreshProgressGlyph : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty=DependencyProperty.Register(nameof(Value),typeof(double),typeof(RefreshProgressGlyph),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty AngleProperty=DependencyProperty.Register(nameof(Angle),typeof(double),typeof(RefreshProgressGlyph),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value {get=>(double)GetValue(ValueProperty);private set=>SetValue(ValueProperty,value);}
    private double Angle {get=>(double)GetValue(AngleProperty);set=>SetValue(AngleProperty,value);}
    public bool Indeterminate { get; private set; }
    private double? _target;
    public RefreshProgressGlyph() {Width=28;Height=28;IsHitTestVisible=false;Unloaded+=(_,_)=>Reset();}
    public void Update(double? value)
    {
        if(_target==value && (value!=null || Indeterminate))return;
        _target=value;
        var unknown=value==null;
        if(Indeterminate!=unknown)
        {
            Indeterminate=unknown;BeginAnimation(AngleProperty,null);Angle=0;
            if(unknown && SystemParameters.ClientAreaAnimation)
                BeginAnimation(AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromSeconds(1.15)) {RepeatBehavior=RepeatBehavior.Forever});
        }
        if(value is { } progress)
        {
            var next=Math.Clamp(progress,0,1);var current=Value;BeginAnimation(ValueProperty,null);Value=next;
            if(SystemParameters.ClientAreaAnimation && Math.Abs(next-current)>.001)
                BeginAnimation(ValueProperty,new DoubleAnimation(current,next,TimeSpan.FromMilliseconds(220)) {EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop});
        }
        InvalidateVisual();
    }
    public void Reset() {BeginAnimation(AngleProperty,null);BeginAnimation(ValueProperty,null);Angle=Value=0;Indeterminate=false;_target=null;}
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);if(ActualWidth<8 || ActualHeight<8) return;
        Brush Brush(string key)=>(Brush)FindResource(key);
        var center=new Point(ActualWidth/2,ActualHeight/2);var radius=Math.Min(ActualWidth,ActualHeight)/2-2;
        dc.DrawEllipse(null,new Pen(Brush("Line"),2),center,radius,radius);
        var fraction=Indeterminate ? .23 : Math.Clamp(Value,0,1);var start=Indeterminate ? Angle-90 : -90;
        var pen=new Pen(Brush("Accent"),2.2) {StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
        Point At(double degrees) => new(center.X+radius*Math.Cos(degrees*Math.PI/180),center.Y+radius*Math.Sin(degrees*Math.PI/180));
        if(fraction>=.9999) dc.DrawEllipse(null,pen,center,radius,radius);
        else if(fraction>0)
        {
            var arc=new StreamGeometry();using(var context=arc.Open()) {context.BeginFigure(At(start),false,false);context.ArcTo(At(start+360*fraction),new Size(radius,radius),0,fraction>.5,SweepDirection.Clockwise,true,false);}
            dc.DrawGeometry(null,pen,arc);
        }
        if(!Indeterminate)
        {
            var text=new FormattedText(((int)Math.Floor(Value*100)).ToString(CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),9,Brush("AccentText"),VisualTreeHelper.GetDpi(this).PixelsPerDip);
            text.SetFontWeight(FontWeights.SemiBold);dc.DrawText(text,new Point(center.X-text.Width/2,center.Y-text.Height/2));
        }
    }
}
