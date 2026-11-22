using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Sf6Prototype;

public partial class OverlayOpacityPicker : UserControl
{
    public int Value { get => (int)OpacitySlider.Value; set => OpacitySlider.Value = value; }
    public bool IncludeCaptureHelp { get; set; } = true;
    public bool GreenScreen
    {
        set
        {
            GreenHint.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            UpdateCaptureHelp();
        }
    }
    public event Action? Changed;
    public event Action? Committed;

    public OverlayOpacityPicker()
    {
        InitializeComponent();
        OpacitySlider.ValueChanged += (_,_) =>
        {
            UpdateCaptureHelp();
            Changed?.Invoke();
            if (HintLayer.IsVisible) UpdateHint(Mouse.GetPosition(OpacitySlider));
            if (Mouse.LeftButton != MouseButtonState.Pressed) Committed?.Invoke();
        };
        OpacitySlider.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_,_) => Committed?.Invoke()), true);
        OpacitySlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_,_) => Committed?.Invoke()));
        IsVisibleChanged += (_,_) => { if (!IsVisible) HintLayer.Visibility = Visibility.Hidden; };
        Unloaded += (_,_) => CaptureHelp.IsExpanded = false;
    }

    private void UpdateCaptureHelp()
    {
        var show = IncludeCaptureHelp && GreenHint.Visibility != Visibility.Visible && Value < 100;
        if (!show) CaptureHelp.IsExpanded = false;
        CaptureHelp.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PointerMoved(object sender, MouseEventArgs e) => UpdateHint(e.GetPosition(OpacitySlider));

    private void UpdateHint(Point pointer)
    {
        if (!IsVisible || OpacitySlider.Template.FindName("PART_Track", OpacitySlider) is not Track track) return;
        var thumb = track.Thumb;
        var onThumb = thumb.IsDragging || thumb.IsMouseOver;
        var value = onThumb ? Value : Math.Clamp((int)Math.Round(track.ValueFromPoint(pointer), MidpointRounding.AwayFromZero), 0, 100);
        var halfThumb = thumb.ActualWidth / 2;
        var center = onThumb ? halfThumb + (track.ActualWidth - thumb.ActualWidth) * Value / 100d
                             : Math.Clamp(pointer.X, halfThumb, Math.Max(halfThumb, track.ActualWidth - halfThumb));
        HoverPercent.Text = $"{value}%";
        Canvas.SetLeft(HintBubble, Math.Clamp(center - HintBubble.Width / 2, 0, Math.Max(0, track.ActualWidth - HintBubble.Width)));
        Canvas.SetLeft(HintArrow, center - HintArrow.Width / 2);
        HintLayer.Visibility = Visibility.Visible;
    }

    private void PointerLeft(object sender, MouseEventArgs e)
    {
        if (!OpacitySlider.IsMouseOver && !OpacitySlider.IsMouseCaptureWithin) HintLayer.Visibility = Visibility.Hidden;
    }
}
