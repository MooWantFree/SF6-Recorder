using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Sf6Prototype;

public sealed class TagBrushConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture) => Brush(value is int n ? Math.Max(0,n) : 0,parameter as string=="Text");
    public static Brush Brush(int color,bool text)
    {
        if(color<12) return (Brush)Application.Current.FindResource($"Tag{color}{(text ? "Text" : "Fill")}");
        double Token(string name)=>(double)Application.Current.FindResource(name);
        var hue=(Token("TagHueStart")+color*Token("TagHueStep")%360/360*Token("TagHueSpan"))/60;var light=Token(text ? "TagTextLightness" : "TagFillLightness");
        var chroma=(1-Math.Abs(2*light-1))*Token("TagSaturation");var x=chroma*(1-Math.Abs(hue%2-1));var m=light-chroma/2;
        var (r,g,b)=hue switch {<1=>(chroma,x,0d),<2=>(x,chroma,0d),<3=>(0d,chroma,x),<4=>(0d,x,chroma),<5=>(x,0d,chroma),_=>(chroma,0d,x)};
        byte Byte(double v)=>(byte)Math.Round((v+m)*255);
        var brush=new SolidColorBrush(Color.FromRgb(Byte(r),Byte(g),Byte(b)));brush.Freeze();return brush;
    }
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture) => throw new NotSupportedException();
}

// The same compact picker edits a replay's tags or filters the current player's favorites.
public sealed class TagPicker : Popup
{
    private readonly TextBox _input=new() {MaxLength=32,Margin=new(0,0,6,0),ToolTip="输入标签名称"};
    private readonly WrapPanel _choices=new();
    private readonly TextBlock _error=new() {TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,0),Visibility=Visibility.Collapsed};
    private readonly Button _add=new() {Content="添加",MinWidth=52};
    private readonly ComboBox _match=new() {Width=114,ItemsSource=new[]{"任一标签","同时包含"},SelectedIndex=0};
    private readonly Grid _filterOptions=new() {Margin=new(0,8,0,0)};
    private readonly HashSet<string> _selected=new(StringComparer.OrdinalIgnoreCase);
    private BattleStore? _store;
    private Battle? _battle;
    private Action? _edited;
    private Action<IReadOnlyList<string>,bool>? _filtered;
    private bool _favoriteContext;
    private bool _updating;
    public TagPicker()
    {
        AllowsTransparency=true;StaysOpen=false;Placement=PlacementMode.Bottom;HorizontalOffset=0;VerticalOffset=4;
        var content=new StackPanel();
        var inputRow=new Grid();inputRow.ColumnDefinitions.Add(new());inputRow.ColumnDefinitions.Add(new() {Width=GridLength.Auto});
        inputRow.Children.Add(_input);Grid.SetColumn(_add,1);inputRow.Children.Add(_add);content.Children.Add(inputRow);
        content.Children.Add(new ScrollViewer {Content=_choices,MaxHeight=240,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new(0,8,0,0)});
        _filterOptions.ColumnDefinitions.Add(new());_filterOptions.ColumnDefinitions.Add(new() {Width=GridLength.Auto});
        _filterOptions.Children.Add(_match);_match.HorizontalAlignment=HorizontalAlignment.Left;
        var clear=new Button {Content="清空",Style=(Style)Application.Current.FindResource("GhostButton")};Grid.SetColumn(clear,1);_filterOptions.Children.Add(clear);
        content.Children.Add(_filterOptions);content.Children.Add(_error);
        Child=new ContentControl {Style=(Style)Application.Current.FindResource("PopupSurface"),Margin=new Thickness(0),Content=content,Width=308,Padding=new Thickness(10)};
        _error.SetResourceReference(TextBlock.ForegroundProperty,"Loss");
        AutomationProperties.SetName(_input,"搜索或添加标签");
        _input.TextChanged+=(_,_)=>RenderChoices();
        _input.KeyDown+=(_,e)=> {if(e.Key==Key.Enter && _battle!=null) {AddTyped();e.Handled=true;}else if(e.Key==Key.Escape) {IsOpen=false;e.Handled=true;}};
        _add.Click+=(_,_)=>AddTyped();
        _match.SelectionChanged+=(_,_)=>FilterChanged();
        clear.Click+=(_,_)=> { _updating=true;_selected.Clear();_match.SelectedIndex=0;_input.Clear();_updating=false;RenderChoices();FilterChanged(); };
        Opened+=(_,_)=>{_input.Focus();};
        Closed+=(_,_)=>{_battle=null;_edited=null;_filtered=null;};
    }
    public void Edit(UIElement target,BattleStore store,Battle battle,Action changed,bool favoriteContext=false)
    {
        _store=store;_battle=battle;_edited=changed;_filtered=null;_favoriteContext=favoriteContext;
        var tags=favoriteContext ? Favorites.ForPlayer(store.FavoriteRecords(),battle.OwnerId).FirstOrDefault(r=>r.Battle.ReplayId==battle.ReplayId)?.Tags : store.TagsFor(battle.OwnerId).GetValueOrDefault(battle.ReplayId);
        _selected.Clear();foreach(var tag in tags ?? []) _selected.Add(tag.Name);
        Open(target,false);
    }
    public void Filter(UIElement target,BattleStore store,IReadOnlyList<string> tags,bool all,Action<IReadOnlyList<string>,bool> changed)
    {
        _updating=true;_store=store;_battle=null;_edited=null;_filtered=changed;
        _selected.Clear();foreach(var tag in tags) _selected.Add(tag);
        _match.SelectedIndex=all ? 1 : 0;_updating=false;Open(target,true);
    }
    private void Open(UIElement target,bool filtering)
    {
        PlacementTarget=target;
        _filterOptions.Visibility=filtering ? Visibility.Visible : Visibility.Collapsed;
        _add.Visibility=filtering ? Visibility.Collapsed : Visibility.Visible;
        _input.ToolTip=filtering ? "搜索标签" : "输入标签名称，Enter 添加";
        _input.Clear();_error.Visibility=Visibility.Collapsed;RenderChoices();IsOpen=true;
    }
    private void RenderChoices()
    {
        _choices.Children.Clear();if(_store==null) return;
        foreach(var tag in TagNames.Suggest(_store.AllTags(),_input.Text))
        {
            var row=new CheckBox {Content=tag.Name,DataContext=tag,IsChecked=_selected.Contains(tag.Name),Margin=new Thickness(0,0,5,5),Style=(Style)Application.Current.FindResource("TagChoice"),ToolTip=tag.Name};
            row.Checked+=(_,_)=>Toggle(tag,true);row.Unchecked+=(_,_)=>Toggle(tag,false);_choices.Children.Add(row);
        }
        if(_choices.Children.Count==0) _choices.Children.Add(new TextBlock {Text=_battle!=null && _input.Text.Length>0 ? "Enter 创建标签" : "暂无匹配标签",Style=(Style)Application.Current.FindResource("Caption"),Margin=new Thickness(2,4,0,4)});
        _match.IsEnabled=_selected.Count>1;
    }
    private void Toggle(MatchTag tag,bool selected)
    {
        try
        {
            if(_battle is { } battle)
            {
                if(_favoriteContext) _store!.SetFavoriteTag(battle.OwnerId,battle.ReplayId,tag.Name,selected);
                else if(selected) _store!.AddTag(battle.OwnerId,battle.ReplayId,tag.Name);else _store!.RemoveTag(battle.OwnerId,battle.ReplayId,tag.Name);
                if(selected) _selected.Add(tag.Name);else _selected.Remove(tag.Name);
                _edited?.Invoke();
            }
            else
            {
                if(selected) _selected.Add(tag.Name);else _selected.Remove(tag.Name);
                _match.IsEnabled=_selected.Count>1;FilterChanged();
            }
        }
        catch(Exception ex) when(ex is ArgumentException or Microsoft.Data.Sqlite.SqliteException) {Error(ex is ArgumentException ? ex.Message : "标签保存失败，请重试。");RenderChoices();}
    }
    private void AddTyped()
    {
        if(_store==null || _battle==null) return;
        try
        {
            var name=TagNames.Normalize(_input.Text);
            if(_favoriteContext) _store.SetFavoriteTag(_battle.OwnerId,_battle.ReplayId,name,true);else _store.AddTag(_battle.OwnerId,_battle.ReplayId,name);
            _selected.Add(name);
            _error.Visibility=Visibility.Collapsed;_edited?.Invoke();_input.Clear();RenderChoices();_input.Focus();
        }
        catch(Exception ex) when(ex is ArgumentException or Microsoft.Data.Sqlite.SqliteException) {Error(ex is ArgumentException ? ex.Message : "标签保存失败，请重试。");}
    }
    private void Error(string text) {_error.Text=text;_error.Visibility=Visibility.Visible;}
    private void FilterChanged() {if(!_updating) _filtered?.Invoke(_selected.ToArray(),_match.SelectedIndex==1);}
    public static Border Pill(MatchTag tag) => new() {CornerRadius=new CornerRadius(10),Padding=new Thickness(7,2,7,2),
        Background=TagBrushConverter.Brush(tag.Color,false),
        Child=new TextBlock {Text=tag.Name,FontSize=11,TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=170,Foreground=TagBrushConverter.Brush(tag.Color,true)}};
}
