using System.Windows;
using System.Windows.Controls;

namespace Sf6Prototype;

// One column definition for both the header and every row. Only player columns flex.
public sealed class HistoryTableGrid : Grid
{
    public static readonly DependencyProperty ModeWidthProperty=DependencyProperty.Register(nameof(ModeWidth),typeof(GridLength),typeof(HistoryTableGrid),
        new PropertyMetadata(new GridLength(56),(d,e)=>((HistoryTableGrid)d).ColumnDefinitions[4].Width=(GridLength)e.NewValue));
    public GridLength ModeWidth { get=>(GridLength)GetValue(ModeWidthProperty); set=>SetValue(ModeWidthProperty,value); }
    public static readonly DependencyProperty ScoreWidthProperty=DependencyProperty.Register(nameof(ScoreWidth),typeof(GridLength),typeof(HistoryTableGrid),
        new PropertyMetadata(new GridLength(148),(d,e)=>((HistoryTableGrid)d).ColumnDefinitions[5].Width=(GridLength)e.NewValue));
    public GridLength ScoreWidth { get=>(GridLength)GetValue(ScoreWidthProperty); set=>SetValue(ScoreWidthProperty,value); }
    public static readonly DependencyProperty TagWidthProperty=DependencyProperty.Register(nameof(TagWidth),typeof(GridLength),typeof(HistoryTableGrid),
        new PropertyMetadata(new GridLength(112),(d,e)=>((HistoryTableGrid)d).ColumnDefinitions[7].Width=(GridLength)e.NewValue));
    public GridLength TagWidth { get=>(GridLength)GetValue(TagWidthProperty); set=>SetValue(TagWidthProperty,value); }
    public HistoryTableGrid()
    {
        foreach(var width in new[]{new GridLength(56),new GridLength(.75,GridUnitType.Star),new GridLength(8),
            new GridLength(1.2,GridUnitType.Star),new GridLength(56),new GridLength(148),new GridLength(62),new GridLength(112)})
            ColumnDefinitions.Add(new ColumnDefinition {Width=width});
        // Portrait, spacing and a four-character role/header must still fit at minimum window width.
        ColumnDefinitions[1].MinWidth=108;
    }
}
