using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Drawing=System.Drawing;
using Forms=System.Windows.Forms;

namespace Sf6Prototype;

public sealed class ModernTrayMenu : Forms.ContextMenuStrip
{
    private Drawing.Font? _labelFont;
    private float _scale=1;
    private const Forms.TextFormatFlags TextFlags=Forms.TextFormatFlags.VerticalCenter|Forms.TextFormatFlags.SingleLine|Forms.TextFormatFlags.NoPrefix|Forms.TextFormatFlags.NoPadding;
    public ModernTrayMenu(Action show,Action exit)
    {
        AutoSize=false;ShowImageMargin=false; ShowCheckMargin=false;
        BackColor=Color("Surface"); ForeColor=Color("Ink"); DropShadowEnabled=true;
        Renderer=new FlatRenderer(this,Color("Surface"),Color("SurfaceSelected"),Color("Line"),Color("Ink"),Color("Ink3"));
        Items.Add(new Forms.ToolStripMenuItem("打开主面板",null,(_,_)=>show()) {Tag="home"});
        Items.Add(new Forms.ToolStripSeparator());
        Items.Add(new Forms.ToolStripMenuItem("退出",null,(_,_)=>exit()) {Tag="exit"});
        ApplyDpi(96);
        Opened+=(_,_)=>
        {
            ApplyDpi((uint)DeviceDpi);
            var rounded=2; // DWMWCP_ROUND, with a normal flat-border fallback.
            DwmSetWindowAttribute(Handle,33,ref rounded,sizeof(int));
        };
    }
    public void ApplyDpi(uint dpi)
    {
        _scale=Math.Max(96,dpi)/96f;
        var previous=_labelFont;
        _labelFont=new Drawing.Font("Microsoft YaHei UI",13*_scale,Drawing.FontStyle.Regular,Drawing.GraphicsUnit.Pixel);
        SuspendLayout();Font=_labelFont;
        Padding=new Forms.Padding(Px(5));
        var textWidth=Items.OfType<Forms.ToolStripMenuItem>().Select(i=>Forms.TextRenderer.MeasureText(i.Text,_labelFont,Drawing.Size.Empty,TextFlags).Width).DefaultIfEmpty().Max();
        var width=Math.Max(Px(148),textWidth+Px(48));var height=Padding.Vertical;
        foreach(Forms.ToolStripItem item in Items)
        {
            item.AutoSize=false;item.Font=_labelFont;item.Padding=Forms.Padding.Empty;
            item.Size=new Drawing.Size(width,Px(item is Forms.ToolStripSeparator ? 7 : 32));
            item.Margin=Forms.Padding.Empty;height+=item.Height;
        }
        Size=new Drawing.Size(width+Padding.Horizontal,height);
        ResumeLayout(true);previous?.Dispose();
    }
    private int Px(int value)=>(int)Math.Round(value*_scale);
    public Drawing.Rectangle TextBounds(Forms.ToolStripItem item) => new(Px(32),0,Math.Max(0,item.Width-Px(42)),item.Height);
    public Drawing.Size MeasureLabel(Forms.ToolStripItem item) => Forms.TextRenderer.MeasureText(item.Text,_labelFont,Drawing.Size.Empty,TextFlags);
    protected override void OnDpiChangedAfterParent(EventArgs e) {base.OnDpiChangedAfterParent(e);ApplyDpi((uint)DeviceDpi);}
    private static Drawing.Color Color(string resource)
    {
        var color=((SolidColorBrush)Application.Current.FindResource(resource)).Color;
        return Drawing.Color.FromArgb(color.A,color.R,color.G,color.B);
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if(disposing) _labelFont?.Dispose();
    }
    private sealed class FlatRenderer(ModernTrayMenu menu,Drawing.Color surface,Drawing.Color selected,Drawing.Color line,Drawing.Color ink,Drawing.Color muted) : Forms.ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e) => e.Graphics.Clear(surface);
        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e) { }
        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen=new Drawing.Pen(line);
            e.Graphics.DrawRectangle(pen,0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1);
        }
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if(!e.Item.Selected || !e.Item.Enabled) return;
            var radius=Math.Max(4,e.Item.Height/8);var diameter=radius*2;
            using var path=new GraphicsPath();
            var width=e.Item.Width-1;var height=e.Item.Height-1;
            path.AddArc(0,0,diameter,diameter,180,90);path.AddArc(width-diameter,0,diameter,diameter,270,90);
            path.AddArc(width-diameter,height-diameter,diameter,diameter,0,90);path.AddArc(0,height-diameter,diameter,diameter,90,90);path.CloseFigure();
            using var brush=new Drawing.SolidBrush(selected);
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;e.Graphics.FillPath(brush,path);
        }
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            Forms.TextRenderer.DrawText(e.Graphics,e.Text,menu._labelFont,menu.TextBounds(e.Item),e.Item.Enabled ? ink : muted,TextFlags);
            // Draw geometry instead of font glyphs: no fallback symbols or doubled point scaling.
            var saved=e.Graphics.Save();e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            e.Graphics.TranslateTransform(menu.Px(9),(e.Item.Height-menu.Px(14))/2f);e.Graphics.ScaleTransform(menu._scale,menu._scale);
            using var pen=new Drawing.Pen(muted,1.35f) {LineJoin=LineJoin.Round,StartCap=LineCap.Round,EndCap=LineCap.Round};
            if(e.Item.Tag as string=="home")
            {
                e.Graphics.DrawLines(pen,new Drawing.PointF[]{new(0,6),new(7,0),new(14,6)});
                e.Graphics.DrawLines(pen,new Drawing.PointF[]{new(2,5),new(2,13),new(6,13),new(6,9),new(9,9),new(9,13),new(12,13),new(12,5)});
            }
            else
            {
                e.Graphics.DrawLines(pen,new Drawing.PointF[]{new(6,1),new(1,1),new(1,13),new(6,13)});
                e.Graphics.DrawLine(pen,5,7,14,7);e.Graphics.DrawLines(pen,new Drawing.PointF[]{new(10,3),new(14,7),new(10,11)});
            }
            e.Graphics.Restore(saved);
        }
        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen=new Drawing.Pen(line);e.Graphics.DrawLine(pen,menu.Px(5),e.Item.Height/2,e.Item.Width-menu.Px(5),e.Item.Height/2);
        }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
}
