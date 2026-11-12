using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Sf6Prototype;

internal static class HistoryAlignmentChecks
{
    public static int Run()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));
        var store=new BattleStore(Path.Combine(root,"alignment.sqlite"));
        const string owner="1234567890";
        var time=DateRanges.Epoch(new DateTime(2026,11,11))!.Value;
        store.SavePage(Enumerable.Range(0,40).Select(i=>new Battle {OwnerId=owner,ReplayId=$"R{i:00}",Character=i<3 ? "隆" : "肯",CharacterId=i<3 ? 1 : 2,
            OpponentCharacter="春丽",Category=i<3 ? "custom" : "rank",Lp=i<3 ? -1 : 23365,PlayedAt=time-i*60,MyRounds=2,OpponentRounds=0}));
        store.AddTag(owner,"R00","复盘");store.AddTag(owner,"R00","防空");
        var archive=new Archive(store,new ProfileCache(root));archive.Load(owner);
        var view=new HistoryView {FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=13};view.Bind(archive);view.Reload();
        TextOptions.SetTextFormattingMode(view,TextFormattingMode.Display);
        var header=(HistoryTableGrid)view.FindName("ColumnHeaders");var list=(ListBox)view.FindName("MatchesList");
        var character=(ComboBox)view.FindName("CharacterFilter");var category=(ListBox)view.FindName("CategoryFilter");
        void Layout(double width)
        {
            view.Measure(new Size(width,630));view.Arrange(new Rect(0,0,width,630));view.UpdateLayout();
            view.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);view.UpdateLayout();
        }
        static bool Near(double a,double b)=>Math.Abs(a-b)<.1;
        double Left(FrameworkElement item)=>item.TranslatePoint(new Point(),view).X;
        double Right(FrameworkElement item)=>item.TranslatePoint(new Point(item.ActualWidth,0),view).X;
        void Aligned(string scenario)
        {
            var row=Descendants<HistoryTableGrid>(view).First(g=>g.DataContext is MatchListRow);
            var xh=Left(header);var xr=Left(row);var equal=true;
            for(var i=0;i<header.ColumnDefinitions.Count;i++)
            {
                equal&=Near(xh,xr) && Near(header.ColumnDefinitions[i].ActualWidth,row.ColumnDefinitions[i].ActualWidth);
                xh+=header.ColumnDefinitions[i].ActualWidth;xr+=row.ColumnDefinitions[i].ActualWidth;
            }
            Check(equal,$"All header and row column boundaries align: {scenario}");
            foreach(var column in new[]{1,3,4,5,6,7})
            {
                var h=header.Children.OfType<TextBlock>().Single(t=>Grid.GetColumn(t)==column);
                if(h.Visibility!=Visibility.Visible)continue;
                var cell=row.Children.OfType<FrameworkElement>().Single(e=>Grid.GetColumn(e)==column);
                var text=column==7 && ((MatchListRow)row.DataContext).Tags.Count==0
                    ? (FrameworkElement)Descendants<Border>(cell).Single(b=>b.Name=="AddTagHint")
                    : cell as TextBlock ?? Descendants<TextBlock>(cell).First();
                Check(column is 5 or 6 ? Near(Right(h),Right(text)) : Near(Left(h),Left(text)),
                    $"Header text anchor matches its cell in column {column}: {scenario}");
            }
        }
        foreach(var width in new[]{720d,980,1140,719.5,980.25})
        {
            character.SelectedIndex=0;Layout(width);
            var scroll=Descendants<ScrollViewer>(list).First();
            Check(scroll.ComputedVerticalScrollBarVisibility==Visibility.Visible,"The long fixture includes a real vertical scrollbar");
            Aligned($"scrollbar, width {width}");
            character.SelectedItem=character.Items.OfType<HistoryView.CharacterChoice>().Single(c=>c.Key=="隆");Layout(width);
            Check(scroll.ComputedVerticalScrollBarVisibility==Visibility.Collapsed,"Filtering to three matches removes the scrollbar");
            Aligned($"no scrollbar, width {width}");
        }
        category.SelectedItem=category.Items.OfType<CategoryChoice>().Single(c=>c.Key=="custom");Layout(720);
        Check(!view.ShowScoreColumns,"The custom category collapses the score column");Aligned("hidden score column");
        character.SelectedIndex=0;category.SelectedItem=category.Items.OfType<CategoryChoice>().Single(c=>c.Key=="rank");Layout(980);
        Check(!view.HasTags && view.ShowScoreColumns,"Untagged ranked matches use the compact tag action");Aligned("compact tag column");
        var scrollbar=Descendants<ScrollBar>(list).Single(b=>b.Orientation==Orientation.Vertical);
        scrollbar.MinWidth=scrollbar.Width=29;Layout(980);
        Check(Near(scrollbar.ActualWidth,29),"The fixture changes the actual scrollbar width without changing a theme token");Aligned("wider scrollbar");
        view.Reload(restore:new() {Search="no matching replay"});Layout(720);
        var viewport=Descendants<ScrollContentPresenter>(list).First();
        Check(list.Items.Count==0 && Near(Left(header),Left(viewport)) && Near(header.ActualWidth,viewport.ActualWidth),"An empty list still aligns headers with the actual viewport");
        Check(App.DataDirectory=="","Alignment checks do not access application data");
        return count;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);if(child is T item)yield return item;
            foreach(var nested in Descendants<T>(child))yield return nested;
        }
    }
}
