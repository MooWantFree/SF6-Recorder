using System.Windows;
using System.Windows.Controls;

namespace Sf6Prototype;

public sealed class MessageWindow : ChromeContentWindow
{
    public MessageWindow(Window? owner,string message,string title,string? confirm=null)
    {
        if(owner?.IsVisible==true) Owner=owner;
        else WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Title=title;
        var body=new StackPanel {Margin=new Thickness(24,4,24,20)};
        body.Children.Add(new TextBlock {Text=message,TextWrapping=TextWrapping.Wrap});
        var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,22,0,0)};
        var cancel=new Button {Content=confirm==null ? Strings.Close : Strings.Cancel,IsCancel=true,IsDefault=true,MinWidth=76};
        actions.Children.Add(cancel);
        if(confirm!=null)
        {
            var accept=new Button {Content=confirm,MinWidth=76,Margin=new Thickness(8,0,0,0),Style=(Style)FindResource("AccentButton")};
            accept.Click+=(_,_)=>DialogResult=true;actions.Children.Add(accept);
        }
        body.Children.Add(actions);Body=body;
    }
    public static bool Confirm(Window owner,string message,string title,string action)=>new MessageWindow(owner,message,title,action).ShowDialog()==true;
    public static void Notice(Window? owner,string message,string title)=>new MessageWindow(owner,message,title).ShowDialog();
}
