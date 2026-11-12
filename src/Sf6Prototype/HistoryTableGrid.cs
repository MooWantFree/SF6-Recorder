using System.Windows;
using System.Windows.Controls;

namespace Sf6Prototype;

// One column definition for both the header and every row. Only player columns flex.
public sealed class HistoryTableGrid : Grid
{
    public static readonly DependencyProperty ScoreWidthProperty=DependencyProperty.Register(nameof(ScoreWidth),typeof(GridLength),typeof(HistoryTableGrid),
        new PropertyMetadata(new GridLength(180),(d,e)=>((HistoryTableGrid)d).ColumnDefinitions[5].Width=(GridLength)e.NewValue));
    public GridLength ScoreWidth { get=>(GridLength)GetValue(ScoreWidthProperty); set=>SetValue(ScoreWidthProperty,value); }
    public HistoryTableGrid()
    {
        foreach(var width in new[]{new GridLength(56),new GridLength(1,GridUnitType.Star),new GridLength(8),
            new GridLength(1.2,GridUnitType.Star),new GridLength(56),new GridLength(180),new GridLength(62),new GridLength(140)})
            ColumnDefinitions.Add(new ColumnDefinition {Width=width});
    }
}
