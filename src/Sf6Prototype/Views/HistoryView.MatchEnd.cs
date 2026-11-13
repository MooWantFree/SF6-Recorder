using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Sf6Prototype;

public partial class HistoryView
{
    private static readonly DependencyProperty MatchEndOffsetProperty=DependencyProperty.RegisterAttached(
        "MatchEndOffset",typeof(double),typeof(HistoryView),new PropertyMetadata(0d,(d,e)=>((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));
    private DispatcherOperation? _matchEndFollowOperation;
    private ScrollViewer? _matchEndFollowViewer;
    private int _matchEndFollowVersion;
    public int InteractionVersion=>_inputVersion;
    private void InitializeMatchEndFollow()
    {
        PreviewMouseDown+=(_,_)=>InterruptMatchEndFollow();
        PreviewMouseWheel+=(_,_)=>InterruptMatchEndFollow();
        PreviewKeyDown+=(_,_)=>InterruptMatchEndFollow();
        Unloaded+=(_,_)=>CancelMatchEndFollow();
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)CancelMatchEndFollow();};
    }
    private void InterruptMatchEndFollow() {_inputVersion++;CancelMatchEndFollow();}
    private void CancelMatchEndFollow()
    {
        _matchEndFollowOperation?.Abort();_matchEndFollowOperation=null;_matchEndFollowVersion++;
        if(_matchEndFollowViewer is not { } viewer) return;
        var offset=viewer.VerticalOffset;
        viewer.BeginAnimation(MatchEndOffsetProperty,null);viewer.SetValue(MatchEndOffsetProperty,offset);
        _matchEndFollowViewer=null;
    }
    public bool RevealLatestMatch(string owner,string replay,int interactionVersion,Func<bool>? stillAllowed=null,bool animate=true)
    {
        CancelMatchEndFollow();
        var category=Category;
        bool Allowed()=>interactionVersion==_inputVersion && !FavoritesOnly && Category==category &&
            _archive?.Owner==owner && _rows.FirstOrDefault()?.Battle.ReplayId==replay && (stillAllowed?.Invoke() ?? true);
        if(!Allowed()) return false;
        var version=_matchEndFollowVersion;
        // Finish pending viewport restoration from the last refresh before moving.
        _matchEndFollowOperation=Dispatcher.BeginInvoke(()=>
        {
            _matchEndFollowOperation=null;
            if(version!=_matchEndFollowVersion || !Allowed()) return;
            _pendingRestore=null;StopAutoLoad();_hasScrolled=false;
            MatchesList.UpdateLayout();
            var viewer=FindViewer(MatchesList);_matchEndFollowViewer=viewer;
            void Complete()
            {
                if(version!=_matchEndFollowVersion) return;
                if(!Allowed()) {CancelMatchEndFollow();return;}
                viewer?.SetValue(MatchEndOffsetProperty,0d);viewer?.BeginAnimation(MatchEndOffsetProperty,null);
                viewer?.ScrollToVerticalOffset(0);
                _matchEndFollowViewer=null;
                var row=_rows[0];var alreadySelected=ReferenceEquals(Selected,row);
                MatchesList.SelectedItem=row;
                if(alreadySelected) SelectionChanged?.Invoke(row);
                // Keep the game in the foreground while the recorder updates behind it.
                if(Window.GetWindow(this)?.IsActive==true && MatchesList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
                    item.Focus();
                UpdateLoadingIndicator();
            }
            if(viewer==null || !animate || !IsVisible || !SystemParameters.ClientAreaAnimation || viewer.VerticalOffset<1)
            {Complete();return;}
            var from=viewer.VerticalOffset;
            viewer.SetValue(MatchEndOffsetProperty,from);
            var motion=new DoubleAnimation(from,0,TimeSpan.FromMilliseconds(280)) {EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut}};
            motion.Completed+=(_,_)=>Complete();
            viewer.BeginAnimation(MatchEndOffsetProperty,motion,HandoffBehavior.SnapshotAndReplace);
        },DispatcherPriority.ContextIdle);
        return true;
    }
}
