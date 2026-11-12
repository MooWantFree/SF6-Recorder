using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Sf6Prototype;

public sealed class FavoritePlayerPicker : Popup
{
    private readonly TextBox _search=new() {ToolTip=Strings.NicknameOrUid,Margin=new Thickness(0,0,0,8)};
    private readonly StackPanel _choices=new();
    private IReadOnlyList<FavoriteRecord> _records=[];
    private PlayerIdentity _signedIn=new("","");
    private string _selected="";
    private Action<PlayerIdentity>? _choose;
    public FavoritePlayerPicker()
    {
        AllowsTransparency=true;StaysOpen=false;Placement=PlacementMode.Bottom;VerticalOffset=4;
        var panel=new StackPanel();
        var input=new Grid();input.Children.Add(_search);
        var placeholder=new TextBlock {Text=Strings.NicknameOrUid,Margin=new Thickness(10,0,0,8),VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};placeholder.SetResourceReference(TextBlock.ForegroundProperty,"Ink3");input.Children.Add(placeholder);
        panel.Children.Add(input);panel.Children.Add(new ScrollViewer {Content=_choices,MaxHeight=300,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        Child=new ContentControl {Style=(Style)Application.Current.FindResource("PopupSurface"),Margin=new Thickness(0),Padding=new Thickness(8),Width=288,Content=panel};
        AutomationProperties.SetName(_search,Strings.SearchFavoritePlayers);
        _search.TextChanged+=(_,_)=>{placeholder.Visibility=_search.Text.Length==0 ? Visibility.Visible : Visibility.Collapsed;Render();};
        _search.KeyDown+=(_,e)=>
        {
            if(e.Key==Key.Escape) {IsOpen=false;e.Handled=true;}
            else if(e.Key==Key.Down) {_choices.Children.OfType<Button>().FirstOrDefault()?.Focus();e.Handled=true;}
            else if(e.Key==Key.Enter && Favorites.Players(_records,_signedIn,_search.Text).FirstOrDefault() is { } player) {Select(player);e.Handled=true;}
        };
        Opened+=(_,_)=>_search.Focus();Closed+=(_,_)=>_choose=null;
    }
    public void Show(UIElement target,IReadOnlyList<FavoriteRecord> records,PlayerIdentity signedIn,string selected,Action<PlayerIdentity> choose)
    {
        PlacementTarget=target;_records=records;_signedIn=signedIn;_selected=selected;_choose=choose;
        _search.Clear();Render();IsOpen=true;
    }
    private void Select(PlayerIdentity player) {var choose=_choose;IsOpen=false;choose?.Invoke(player);}
    private void Render()
    {
        _choices.Children.Clear();var players=Favorites.Players(_records,_signedIn,_search.Text);var hadSelf=false;
        foreach(var player in players)
        {
            if(hadSelf && player.Id!=_signedIn.Id)
            {var separator=new Border {Height=1,Margin=new Thickness(4,5,4,5)};separator.SetResourceReference(Border.BackgroundProperty,"Line");_choices.Children.Add(separator);hadSelf=false;}
            var label=new StackPanel();
            label.Children.Add(new TextBlock {Text=player.DisplayNameFor(_signedIn.Id),TextTrimming=TextTrimming.CharacterEllipsis,FontWeight=player.Id==_selected ? FontWeights.SemiBold : FontWeights.Normal});
            label.Children.Add(new TextBlock {Text=player.Id,Style=(Style)Application.Current.FindResource("Caption"),Margin=new Thickness(0,2,0,0)});
            var button=new Button {Content=label,Style=(Style)Application.Current.FindResource("GhostButton"),Padding=new Thickness(8,6,8,6),HorizontalContentAlignment=HorizontalAlignment.Stretch,ToolTip=player.DisplayName+"\n"+player.Id};
            if(player.Id==_selected) button.SetResourceReference(Control.BackgroundProperty,"SurfaceSelected");
            button.Click+=(_,_)=>Select(player);_choices.Children.Add(button);hadSelf=player.Id==_signedIn.Id;
        }
        if(players.Count==0) _choices.Children.Add(new TextBlock {Text=Strings.NoMatchingPlayers,Style=(Style)Application.Current.FindResource("Caption"),Margin=new Thickness(8)});
    }
}
