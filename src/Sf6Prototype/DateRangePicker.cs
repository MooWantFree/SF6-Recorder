using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Sf6Prototype;

public sealed class DateRangePicker : UserControl
{
    private readonly Button _button = new() { Margin = new(0), Padding = new(10,5,10,5), HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly Popup _popup = new() { AllowsTransparency = true, StaysOpen = false, Placement = PlacementMode.Bottom, VerticalOffset = 5 };
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { Text = "先选第一天，再选最后一天", Foreground = Brushes.Gray, Margin = new(2,0,0,10) };
    private readonly UniformGrid _days = new() { Columns = 7 };
    private readonly List<(Button Button,DateTime Date)> _dayButtons = [];
    private readonly Button _next = new() { Content = "›", Width = 30, Margin = new(0), BorderThickness = new(0) };
    private DateTime _month, _today;
    private DateTime? _anchor, _preview;
    public DateTime? Start { get; private set; }
    public DateTime? End { get; private set; }
    public event EventHandler? RangeChanged;
    public DateRangePicker()
    {
        Content = _button; _button.Content = "▦  全部日期"; _popup.PlacementTarget = _button;
        AutomationProperties.SetName(_button, "日期范围");
        var panel = new StackPanel(); panel.Children.Add(_hint);
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0,0,0,12) };
        foreach (var item in new[] { ("昨天","yesterday"),("本月","month"),("上个月","previous"),("90 天","90"),("全部","all") })
        {
            var b = new Button { Content = item.Item1, Padding = new(8,5,8,5), Margin = new(0,0,5,0), BorderThickness = new(0), Background = Brush("#F1F1F5") };
            b.Click += (_,_) => { var range = DateRanges.Preset(item.Item2,_today); SetRange(range.Start,range.End); _popup.IsOpen = false; }; presets.Children.Add(b);
        }
        panel.Children.Add(presets);
        var head = new Grid { Margin = new(0,6,0,10) }; head.Children.Add(_title);
        var previous = new Button { Content = "‹", Width = 30, Margin = new(0), HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new(0) };
        previous.Click += (_,_) => { _month = _month.AddMonths(-1); DrawMonth(); }; AutomationProperties.SetName(previous,"上个月日历"); head.Children.Add(previous);
        _next.HorizontalAlignment = HorizontalAlignment.Right; _next.Click += (_,_) => { _month = _month.AddMonths(1); DrawMonth(); }; AutomationProperties.SetName(_next,"下个月日历"); head.Children.Add(_next); panel.Children.Add(head);
        var week = new UniformGrid { Columns = 7, Margin = new(0,0,0,5) };
        foreach (var text in new[] { "一","二","三","四","五","六","日" }) week.Children.Add(new TextBlock { Text = text, Foreground = Brush("#A2A2AE"), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(week); panel.Children.Add(_days);
        _popup.Child = new Border { Child = panel, Width = 344, Background = Brushes.White, BorderBrush = Brush("#DDDDE5"), BorderThickness = new(1), CornerRadius = new(8), Padding = new(12) };
        _popup.Opened += (_,_) => UiCapture.SaveLater((FrameworkElement)_popup.Child,"ui-date-range.png");
        _popup.PreviewKeyDown += (_,e) => { if (e.Key == Key.Escape) { _popup.IsOpen = false; e.Handled = true; } };
        _button.Click += (_,_) =>
        {
            _today = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow,"Taipei Standard Time").Date;
            var date = End ?? _today; _month = new(date.Year,date.Month,1); _anchor = _preview = null; _hint.Text = "先选第一天，再选最后一天";
            DrawMonth(); _popup.IsOpen = !_popup.IsOpen;
        };
    }
    private static Brush Brush(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    public void SetRange(DateTime? start, DateTime? end)
    {
        if (start.HasValue && end.HasValue) { var r = DateRanges.Normalize(start.Value,end.Value); Start = r.Start; End = r.End; }
        else Start = End = null;
        _button.Content = Start == null ? "▦  全部日期" : Start == End ? $"▦  {Start:yyyy-MM-dd}" : $"▦  {Start:MM/dd} — {End:MM/dd}";
        _button.ToolTip = Start == null ? "选择日期范围" : $"{Start:yyyy-MM-dd} — {End:yyyy-MM-dd}";
        RangeChanged?.Invoke(this,EventArgs.Empty);
    }
    private void DrawMonth()
    {
        _title.Text = _month.ToString("yyyy年M月",CultureInfo.InvariantCulture); _next.IsEnabled = _month.AddMonths(1) <= _today;
        _days.Children.Clear(); _dayButtons.Clear();
        var beginning = _month.AddDays(-(((int)_month.DayOfWeek+6)%7));
        for (var i = 0; i < 42; i++)
        {
            var date = beginning.AddDays(i);
            var b = new Button { Content = date.Day.ToString(), Height = 36, MinHeight = 0, Padding = new(0), Margin = new(1), BorderThickness = new(0), Background = Brushes.Transparent, FontSize = 13, IsEnabled = date <= _today && date.Month == _month.Month };
            b.Foreground = date.Month == _month.Month ? Brush("#303039") : Brush("#C4C4CD");
            AutomationProperties.SetName(b,date.ToString("yyyy-MM-dd"));
            b.Click += (_,_) =>
            {
                if (_anchor == null) { _anchor = _preview = date; _hint.Text = "选择最后一天"; PaintRange(); }
                else { SetRange(_anchor,date); _anchor = _preview = null; _popup.IsOpen = false; }
            };
            b.MouseEnter += (_,_) => { if (_anchor != null) { _preview = date; PaintRange(); } };
            _days.Children.Add(b); _dayButtons.Add((b,date));
        }
        PaintRange();
    }
    private void PaintRange()
    {
        var start = _anchor ?? Start; var end = _anchor == null ? End : _preview ?? _anchor;
        if (start > end) (start,end) = (end,start);
        foreach(var (b,date) in _dayButtons)
        {
            var edge = date == start || date == end;
            b.Background = edge ? Brush("#6262D9") : date >= start && date <= end ? Brush("#EEEEFA") : Brushes.Transparent;
            b.Foreground = edge ? Brushes.White : date == _today ? Brush("#5B5BD6") : date.Month == _month.Month ? Brush("#303039") : Brush("#C4C4CD");
            b.FontWeight = date == _today || edge ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (_popup.IsOpen) UiCapture.SaveLater((FrameworkElement)_popup.Child,"ui-date-range.png");
    }
}

