using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Sf6Prototype;

public partial class HistoryView : UserControl
{
    public static readonly DependencyProperty ScoreColumnWidthProperty = DependencyProperty.Register(nameof(ScoreColumnWidth), typeof(GridLength), typeof(HistoryView), new(new GridLength(148)));
    public static readonly DependencyProperty TagColumnWidthProperty = DependencyProperty.Register(nameof(TagColumnWidth), typeof(GridLength), typeof(HistoryView), new(new GridLength(52)));
    public static readonly DependencyProperty ModeColumnWidthProperty = DependencyProperty.Register(nameof(ModeColumnWidth), typeof(GridLength), typeof(HistoryView), new(new GridLength(56)));
    public static readonly DependencyProperty ShowModeColumnProperty = DependencyProperty.Register(nameof(ShowModeColumn), typeof(bool), typeof(HistoryView), new(true));
    public GridLength ModeColumnWidth { get => (GridLength)GetValue(ModeColumnWidthProperty); private set => SetValue(ModeColumnWidthProperty,value); }
    public bool ShowModeColumn { get => (bool)GetValue(ShowModeColumnProperty); private set => SetValue(ShowModeColumnProperty,value); }
    public static readonly DependencyProperty HasTagsProperty = DependencyProperty.Register(nameof(HasTags), typeof(bool), typeof(HistoryView), new(false));
    public GridLength TagColumnWidth { get => (GridLength)GetValue(TagColumnWidthProperty); private set => SetValue(TagColumnWidthProperty,value); }
    public bool HasTags { get => (bool)GetValue(HasTagsProperty); private set => SetValue(HasTagsProperty,value); }
    public static readonly DependencyProperty ShowScoreColumnsProperty = DependencyProperty.Register(nameof(ShowScoreColumns), typeof(bool), typeof(HistoryView), new(true));
    public GridLength ScoreColumnWidth { get => (GridLength)GetValue(ScoreColumnWidthProperty); set => SetValue(ScoreColumnWidthProperty,value); }
    public bool ShowScoreColumns { get => (bool)GetValue(ShowScoreColumnsProperty); set => SetValue(ShowScoreColumnsProperty,value); }
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Archive? _archive;
    private TagPicker? _tagPicker;
    private FavoritePlayerPicker? _playerPicker;
    private Archive? _favoriteArchive;
    private IReadOnlyList<FavoriteRecord> _favoriteRecords=[],_playerFavorites=[];
    public PlayerIdentity? DefaultFavoritePlayer { get; set; }
    public PlayerIdentity? FavoritePlayer { get; private set; }
    public Archive? ActiveArchive => FavoritesOnly ? _favoriteArchive : _archive;
    private IReadOnlyList<string> _filterTags=[];
    private bool _matchAllTags;
    public bool FavoritesOnly { get; private set; }
    public event Action? TagsChanged;
    private readonly ObservableCollection<MatchListRow> _rows=[];
    private readonly Dictionary<string,DayGroup> _groups=[];
    private string _rowsOwner="";
    private int _layoutVersion, _inputVersion;
    private bool _scrollDemand, _moreQueued, _hasScrolled;
    private bool _ready, _rebuilding, _headOnlyRefresh;
    private HistoryViewState? _pendingRestore;
    public event Action<MatchListRow?>? SelectionChanged;
    public event Action? MoreRequested;
    public event Action? CategoryChanged;
    public event Action? PlayerChanged;
    public BattlePager? Pager { get; set; }
    public IReadOnlyList<Battle> Filtered { get; private set; } = [];
    public string Category => FavoritesOnly ? "" : (CategoryFilter.SelectedItem as CategoryChoice)?.Key ?? "";
    private bool _loading;
    public MatchListRow? Selected => MatchesList.SelectedItem as MatchListRow;

    public HistoryView()
    {
        InitializeComponent();
        InitializeMatchEndFollow();
        var view=new ListCollectionView(_rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MatchListRow.Group)));
        MatchesList.ItemsSource=view;
        MatchesList.PreviewMouseWheel += (_,e) => { _inputVersion++; _pendingRestore=null; if(e.Delta<0) { _hasScrolled=true; DemandMore(); } else StopAutoLoad(); };
        MatchesList.PreviewMouseDown += (_,e) =>
        {
            _inputVersion++; _pendingRestore=null;
            // Re-clicking the still-highlighted list row returns from detail-only history browsing.
            if(_ready && !_rebuilding && e.ChangedButton==System.Windows.Input.MouseButton.Left &&
                e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(MatchesList,source) is ListBoxItem {IsSelected:true,DataContext:MatchListRow row})
                SelectionChanged?.Invoke(row);
        };
        MatchesList.PreviewKeyDown += (_,e) =>
        {
            _inputVersion++; _pendingRestore=null;
            if(e.Key is System.Windows.Input.Key.End or System.Windows.Input.Key.PageDown or System.Windows.Input.Key.Down) { _hasScrolled=true; DemandMore(); }
            else StopAutoLoad();
        };
        MatchesList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_,e) =>
        {
            if(e.OriginalSource is not ScrollViewer viewer) return;
            UpdateColumnHeaderBounds(viewer);
            UpdateLoadingIndicator();
            if(!_rebuilding)
            {
                if(e.VerticalChange>0 && e.ExtentHeightChange==0)
                {
                    if(System.Windows.Input.Mouse.LeftButton==System.Windows.Input.MouseButtonState.Pressed) _hasScrolled=true;
                    DemandMore();
                }
                else if(!NearEnd(viewer)) _scrollDemand=false;
            }
        }));
        MatchesList.Loaded += (_,_) => { UpdateColumnHeaderBounds(FindViewer(MatchesList)); UpdateLoadingIndicator(); if(_ready && _pendingRestore!=null) ApplyFilter(preserveViewport:true); };
        MatchesList.SizeChanged += (_,_) => UpdateColumnHeaderBounds(FindViewer(MatchesList));
        ColumnHeaderHost.SizeChanged += (_,_) => UpdateColumnHeaderBounds(FindViewer(MatchesList));
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };
        Unloaded += (_, _) => {_searchTimer.Stop();ClosePickers();};
        IsVisibleChanged += (_,_) => { if(IsVisible && _ready && _pendingRestore!=null) ApplyFilter(preserveViewport:true); };
    }
    private void UpdateColumnHeaderBounds(ScrollViewer? viewer)
    {
        if(viewer?.Template?.FindName("PART_ScrollContentPresenter",viewer) is not ScrollContentPresenter viewport ||
            viewport.ActualWidth<=0 || ColumnHeaderHost.ActualWidth<=0) return;
        // Use the actual viewport, including the native theme's scrollbar minimum and list insets.
        var left=viewport.TranslatePoint(new Point(),ColumnHeaderHost).X;
        var right=viewport.TranslatePoint(new Point(viewport.ActualWidth,0),ColumnHeaderHost).X;
        var margin=new Thickness(Math.Max(0,left),0,Math.Max(0,ColumnHeaderHost.ActualWidth-right),0);
        if(Math.Abs(ColumnHeaders.Margin.Left-margin.Left)>.01 || Math.Abs(ColumnHeaders.Margin.Right-margin.Right)>.01)
            ColumnHeaders.Margin=margin;
    }
    private static ScrollViewer? FindViewer(DependencyObject parent)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is ScrollViewer viewer) return viewer;
            if(FindViewer(child) is { } nested) return nested;
        }
        return null;
    }
    public void Bind(Archive archive) => _archive = archive;
    public void RefreshLanguage()
    {
        var state=CaptureState();
        ClosePickers(); _tagPicker=null; _playerPicker=null;
        _groups.Clear(); _rows.Clear();
        _ready=false; DateRange.RefreshLanguage();
        Reload(restore:state);
    }
    public sealed record OutcomeChoice(string Key)
    {
        public override string ToString()=>Key.Length==0 ? Strings.All : Localization.Outcome(Key);
    }
    public sealed record CharacterChoice(string Key,string Name)
    {
        public override string ToString()=>Name;
    }
    public void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }
    public void SetLoading(bool loading)
    {
        _loading=loading; UpdateLoadingIndicator();
    }
    private static bool NearEnd(ScrollViewer viewer) => viewer.ScrollableHeight-viewer.VerticalOffset<Math.Max(160,viewer.ViewportHeight/2);
    private void UpdateLoadingIndicator()
    {
        var hasRows=_rows.Count>0;
        var loading=_loading && !FavoritesOnly;
        InitialLoadingIndicator.Visibility=loading && !hasRows ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Visibility=_ready && !loading && !hasRows ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text=FavoritesOnly ? Strings.NoMatchingFavorites : _archive?.All.Count==0 ? Strings.NoRecordsRefresh : Strings.NoMatchingRecords;
        var viewer=FindViewer(MatchesList);
        var atBottom=viewer!=null && viewer.ScrollableHeight-viewer.VerticalOffset<=1;
        LoadingIndicator.Visibility=loading && hasRows && _hasScrolled && atBottom ? Visibility.Visible : Visibility.Hidden;
        EndIndicator.Visibility=!loading && _ready && hasRows && atBottom && (FavoritesOnly || Pager!=null && !Pager.HasMore(Filtered,Category)) ? Visibility.Visible : Visibility.Hidden;
    }
    public void StopAutoLoad() => _scrollDemand=false;
    public void AllowPageUpdates() => _headOnlyRefresh=false;
    private void DemandMore() { _scrollDemand=true; UpdateLoadingIndicator(); ContinueAutoLoad(); }
    public void ContinueAutoLoad()
    {
        if(_moreQueued || !_scrollDemand) return;
        _moreQueued=true;
        Dispatcher.BeginInvoke(() =>
        {
            _moreQueued=false;
            if(!_scrollDemand || _loading || _rebuilding || !IsVisible || !_ready || FavoritesOnly) return;
            if(FindViewer(MatchesList) is not { } viewer || !NearEnd(viewer)) { _scrollDemand=false; return; }
            if(Pager?.HasMore(Filtered,Category)==true) MoreRequested?.Invoke();
            else _scrollDemand=false;
        },DispatcherPriority.Background);
    }
    public HistoryViewState CaptureState()
    {
        if(_pendingRestore!=null) return _pendingRestore;
        var viewer=FindViewer(MatchesList);
        return new()
        {
            Category=Category, Character=CharacterFilter.SelectedIndex>0 ? (CharacterFilter.SelectedItem as CharacterChoice)?.Key : null,
            OpponentCharacter=OpponentCharacterFilter.SelectedIndex>0 ? (OpponentCharacterFilter.SelectedItem as CharacterChoice)?.Key : null,
            FavoritesOnly=FavoritesOnly,Tags=_filterTags.ToArray(),MatchAllTags=_matchAllTags,
            FavoritePlayer=FavoritePlayer,
            Outcome=OutcomeFilter.SelectedIndex>0 ? (OutcomeFilter.SelectedItem as OutcomeChoice)?.Key : null,
            Search=SearchBox.Text, Start=DateRange.Start, End=DateRange.End,
            SelectedReplay=Selected?.Battle.ReplayId, Anchor=CaptureAnchor(viewer), ScrollOffset=viewer?.VerticalOffset ?? 0,
            VisibleReplays=_rows.Select(r=>r.Battle.ReplayId).ToArray()
        };
    }
    private HistoryScrollAnchor? CaptureAnchor(ScrollViewer? viewer) => viewer==null ? null : VisibleContainers(MatchesList)
        .Select(item=>(Item:item,Top:item.TranslatePoint(new Point(),viewer).Y))
        .Where(p=>p.Top+p.Item.ActualHeight>0 && p.Top<viewer.ViewportHeight)
        .OrderBy(p=>p.Top).Select(p=>new HistoryScrollAnchor(((MatchListRow)p.Item.DataContext).Battle.ReplayId,p.Top)).FirstOrDefault();
    public void Reload(bool preserveViewport = false,HistoryViewState? restore=null,bool automaticRefresh=false)
    {
        if (_archive is not { } a) return;
        if(automaticRefresh) _headOnlyRefresh=true;
        if(restore!=null) _headOnlyRefresh=false;
        if(restore!=null) { _pendingRestore=restore; StopAutoLoad(); }
        restore=_pendingRestore;
        _ready = false;
        if(restore!=null) {FavoritesOnly=restore.FavoritesOnly;FavoritePlayer=restore.FavoritePlayer;_filterTags=restore.Tags.ToArray();_matchAllTags=restore.MatchAllTags;}
        var oldCategory = FavoritesOnly ? "favorites" : restore?.Category ?? Category;
        var oldCharacter = restore!=null ? restore.Character : (CharacterFilter.SelectedItem as CharacterChoice)?.Key;
        var oldOpponentCharacter=restore!=null ? restore.OpponentCharacter : (OpponentCharacterFilter.SelectedItem as CharacterChoice)?.Key;
        var oldOutcome = restore!=null ? restore.Outcome : (OutcomeFilter.SelectedItem as OutcomeChoice)?.Key;
        _favoriteRecords=a.Store.FavoriteRecords(summariesOnly:true);
        var defaultPlayer=DefaultFavoritePlayer ?? new PlayerIdentity(a.SignedInId ?? a.Owner,a.All.FirstOrDefault(b=>b.OwnerId==(a.SignedInId ?? a.Owner))?.PlayerName ?? "");
        FavoritePlayer ??=defaultPlayer;
        if(FavoritesOnly)
        {
            FavoritePlayer=Favorites.Players(_favoriteRecords,defaultPlayer).FirstOrDefault(p=>p.Id==FavoritePlayer.Id) ?? FavoritePlayer;
            _favoriteArchive ??=new Archive(a.Store,a.Profiles);
            _favoriteArchive.SignedInId=a.SignedInId;
            _playerFavorites=_favoriteArchive.LoadFavorites(FavoritePlayer.Id,_favoriteRecords);
        }
        var categories = BattleCategory.All.Select(c => new CategoryChoice(c.Key, c.ShortName, a.All.Count(b => b.Category == c.Key),HistoryIncomplete(c.Key)))
            .Prepend(new CategoryChoice("", Strings.All, a.All.Count,HistoryIncomplete(""))).Append(new CategoryChoice("favorites",Strings.Favorites,Favorites.ForPlayer(_favoriteRecords,FavoritePlayer.Id).Count)).ToList();
        CategoryFilter.ItemsSource = categories;
        CategoryFilter.SelectedItem = categories.FirstOrDefault(c => c.Key == oldCategory) ?? categories[0];
        var filterRows=FavoritesOnly ? _playerFavorites.Select(r=>r.Battle).ToList() : a.All;
        var characters = filterRows.DistinctBy(b=>b.Character).Select(b=>new CharacterChoice(b.Character,a.Profiles.Names.Display(b.CharacterId,b.Character))).OrderBy(c=>c.Name).Prepend(new CharacterChoice("",Strings.AllCharacters)).ToList();
        CharacterFilter.ItemsSource = characters;
        CharacterFilter.SelectedItem = characters.FirstOrDefault(c=>c.Key==oldCharacter) ?? characters[0];
        var otherCharacters=filterRows.DistinctBy(b=>b.OpponentCharacter).Select(b=>new CharacterChoice(b.OpponentCharacter,a.Profiles.Names.Display(b.OpponentCharacterId,b.OpponentCharacter))).OrderBy(c=>c.Name).Prepend(new CharacterChoice("",Strings.AnyOpponent)).ToList();
        OpponentCharacterFilter.ItemsSource=otherCharacters;
        OpponentCharacterFilter.SelectedItem=otherCharacters.FirstOrDefault(c=>c.Key==oldOpponentCharacter) ?? otherCharacters[0];
        var outcomes = new[] { "", "胜", "负" }.Concat(new[] { "平", "待确认" }.Where(o => filterRows.Any(b => b.Outcome == o))).Select(o=>new OutcomeChoice(o)).ToList();
        OutcomeFilter.ItemsSource = outcomes;
        OutcomeFilter.SelectedItem = outcomes.FirstOrDefault(o=>o.Key==oldOutcome) ?? outcomes[0];
        if(restore!=null) { DateRange.SetRange(restore.Start,restore.End); SearchBox.Text=restore.Search; _searchTimer.Stop(); }
        RefreshTagFilter();
        _ready = true; ApplyFilter(preserveViewport);
    }
    public void ResetFilters(string category="")
    {
        InterruptMatchEndFollow();
        _headOnlyRefresh=false;
        StopAutoLoad();
        _pendingRestore=null; _layoutVersion++;
        _hasScrolled=false;
        _ready = false;
        CategoryFilter.SelectedIndex = CharacterFilter.SelectedIndex = OpponentCharacterFilter.SelectedIndex = OutcomeFilter.SelectedIndex = 0;
        FavoritesOnly=false;FavoritePlayer=null;_filterTags=[];_matchAllTags=false;ClosePickers();RefreshTagFilter();
        if(CategoryFilter.Items.OfType<CategoryChoice>().FirstOrDefault(c=>c.Key==category) is { } selected) CategoryFilter.SelectedItem=selected;
        DateRange.SetRange(null, null); SearchBox.Text = ""; _searchTimer.Stop();
        _ready = true;
    }
    private bool HistoryIncomplete(string category)
    {
        if(_archive is not { } archive) return false;
        if(Pager?.Owner==archive.Owner) return !Pager.IsComplete(category);
        return category.Length==0 ? archive.Store.IsCategoryPending(archive.Owner,"") || BattleCategory.All.Any(c=>!archive.Store.HasCategorySnapshot(archive.Owner,c.Key)) :
            !archive.Store.HasCategorySnapshot(archive.Owner,category);
    }
    private void ApplyFilter(bool preserveViewport = false)
    {
        if (!_ready || ActiveArchive is not { } a) return;
        var restore=_pendingRestore;
        if(!preserveViewport) { CancelMatchEndFollow();StopAutoLoad(); _hasScrolled=false; _headOnlyRefresh=false; }
        var viewer = preserveViewport || restore!=null ? FindViewer(MatchesList) : null;
        var offset = restore?.ScrollOffset ?? viewer?.VerticalOffset ?? 0;
        var anchor=restore!=null ? restore.Anchor : CaptureAnchor(viewer);
        var version=++_layoutVersion; var inputVersion=_inputVersion;
        static long? Epoch(DateTime? value) => value == null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        var category = Category;
        var character = CharacterFilter.SelectedIndex > 0 ? (CharacterFilter.SelectedItem as CharacterChoice)?.Key : null;
        var opponentCharacter=OpponentCharacterFilter.SelectedIndex>0 ? (OpponentCharacterFilter.SelectedItem as CharacterChoice)?.Key : null;
        var outcome = OutcomeFilter.SelectedIndex > 0 ? (OutcomeFilter.SelectedItem as OutcomeChoice)?.Key : null;
        var filter=new BattleFilter(a.Owner,string.IsNullOrEmpty(category) ? null : category,character,outcome,SearchBox.Text,
            Epoch(DateRange.Start),Epoch(DateRange.End?.AddDays(1)),opponentCharacter,FavoritesOnly,FavoritesOnly ? _filterTags : [],_matchAllTags);
        var filtered=FavoritesOnly ? Favorites.Filter(_playerFavorites,filter) : a.Store.QuerySummaries(filter);

        var wins = filtered.Count(b => b.Outcome == "胜"); var losses = filtered.Count(b => b.Outcome == "负"); var draws = filtered.Count(b => b.Outcome == "平");
        var incomplete=!FavoritesOnly && HistoryIncomplete(category);
        TotalMetric.Text = filtered.Count.ToString("N0")+(incomplete ? "*" : "");
        TotalMetric.ToolTip=incomplete ? Strings.IncompleteHistoryCount : null;
        System.Windows.Automation.AutomationProperties.SetHelpText(TotalMetric,incomplete ? Strings.IncompleteHistoryCount : "");
        TotalMetricUnit.Text = filtered.Count==1 ? Strings.MatchSuffix : Strings.MatchesSuffix;
        WinRateMetric.Text = wins + losses + draws == 0 ? "—" : $"{100d * wins / (wins + losses + draws):F1}%";
        ResultsMetric.Text = Localization.Format(Strings.WinLossSummary, wins, losses) + (draws > 0 ? Localization.Format(Strings.DrawCountSuffix, draws) : "");
        var recent = filtered.Take(20).Reverse().Select(b => new OutcomeDot(Kinds.Of(b.Outcome), $"{BattleDateDisplay.Timestamp(b.Time)}  {Localization.Outcome(b.Outcome)}  {b.Opponent}")).ToList();
        RecentDots.ItemsSource = recent; RecentLabel.Text = Localization.Format(Strings.RecentCount, recent.Count);
        RecentPanel.Visibility = recent.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        // Column geometry follows the user's filter, not whichever page arrived first.
        ShowModeColumn=string.IsNullOrEmpty(category);
        ModeColumnWidth=new GridLength(ShowModeColumn ? Localization.Culture.Name=="ja" ? 68 : 56 : 0);
        ShowScoreColumns = category=="rank" || string.IsNullOrEmpty(category);
        ScoreColumnWidth = new GridLength(ShowScoreColumns ? 148 : 0);
        HasTags=filtered.Any(b=>a.Tags.GetValueOrDefault(b.ReplayId)?.Count>0);
        TagColumnWidth=new GridLength(HasTags ? 112 : 52);
        ResetButton.Visibility = character != null || opponentCharacter!=null || FavoritesOnly && _filterTags.Count>0 || outcome != null || DateRange.Start != null || !string.IsNullOrWhiteSpace(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        var previous = restore!=null ? restore.SelectedReplay : Selected?.Battle.ReplayId;
        Filtered=filtered;
        var orderedPrefix=character==null && opponentCharacter==null && outcome==null && DateRange.Start==null && string.IsNullOrWhiteSpace(SearchBox.Text);
        if(_rowsOwner!=a.Owner) { _groups.Clear(); _rowsOwner=a.Owner; }
        var restoredIds=restore?.VisibleReplays.ToHashSet();
        var retained=restoredIds!=null ? filtered.Where(b=>restoredIds.Contains(b.ReplayId)).ToList() : preserveViewport ? _rows.Select(r=>r.Battle).ToList() : null;
        var visible=(FavoritesOnly ? filtered : Pager?.Visible(filtered,Category,orderedPrefix,retained,prependOnly:_headOnlyRefresh) ?? filtered).ToList();
        var rows = BuildRows(a, visible);
        _rebuilding = true;
        IncrementalList.Apply(_rows,rows,r=>(r.Battle.OwnerId,r.Battle.ReplayId),(old,next)=>old.Update(next));
        MatchesList.SelectedItem = _rows.FirstOrDefault(r => r.Battle.ReplayId == previous) ?? _rows.FirstOrDefault();
        if ((preserveViewport || restore!=null) && viewer != null && IsVisible)
            Dispatcher.BeginInvoke(() =>
            {
                if(version!=_layoutVersion) return;
                if(inputVersion==_inputVersion)
                {
                    var container=anchor==null ? null : VisibleContainers(MatchesList).FirstOrDefault(i=>((MatchListRow)i.DataContext).Battle.ReplayId==anchor.ReplayId);
                    if(container==null && anchor!=null && _rows.FirstOrDefault(r=>r.Battle.ReplayId==anchor.ReplayId) is { } anchorRow)
                    {
                        MatchesList.ScrollIntoView(anchorRow); MatchesList.UpdateLayout();
                        container=VisibleContainers(MatchesList).FirstOrDefault(i=>((MatchListRow)i.DataContext).Battle.ReplayId==anchor.ReplayId);
                    }
                    if(container!=null) viewer.ScrollToVerticalOffset(viewer.VerticalOffset+container.TranslatePoint(new Point(),viewer).Y-anchor!.Top);
                    else viewer.ScrollToVerticalOffset(offset);
                    MatchesList.UpdateLayout();
                }
                if(ReferenceEquals(_pendingRestore,restore)) _pendingRestore=null;
                _rebuilding=false; UpdateLoadingIndicator();
            }, DispatcherPriority.Loaded);
        else
        {
            if(restore==null && MatchesList.SelectedItem != null) MatchesList.ScrollIntoView(MatchesList.SelectedItem);
            _rebuilding=false;
        }
        SelectionChanged?.Invoke(Selected);
        UpdateLoadingIndicator();
    }
    private static IEnumerable<ListBoxItem> VisibleContainers(DependencyObject parent)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if(child is ListBoxItem {DataContext:MatchListRow} item) yield return item;
            else foreach(var nested in VisibleContainers(child)) yield return nested;
        }
    }
    private List<MatchListRow> BuildRows(Archive a, List<Battle> battles)
    {
        var culture = Localization.Culture; var year = BattleDateDisplay.CurrentYear;
        var rows = new List<MatchListRow>(battles.Count);
        foreach (var day in battles.GroupBy(b => b.Time[..10]))
        {
            var items = day.Select(a.Row).ToList();
            var date = DateTime.ParseExact(day.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var summary = Localization.Format(Strings.MatchSummary, items.Count, items.Count(r => r.Kind == "win"), items.Count(r => r.Kind == "loss"));
            var settled = items.Where(r => r.Delta != null).ToList();
            // Summing points is only meaningful for one character on one scale.
            if (settled.Count > 0 && settled.Select(r => (r.Battle.CharacterId, r.RatingUnit)).Distinct().Count() == 1)
                summary += $" · {settled.Sum(r => r.Delta):+0;−0;0} {settled[0].RatingUnit}";
            if(!_groups.TryGetValue(day.Key,out var group)) _groups[day.Key]=group=new(date.ToString(date.Year == year ? Strings.DayGroupDate : Strings.DayGroupDateYear, culture));
            group.Summary=summary;
            rows.AddRange(items.Select(r => new MatchListRow(r.Battle,r.OwnImage,r.OpponentImage,r.Point,r.SignedInId,r.SettledRank,r.Character,r.OpponentCharacter) {Group=group,Tags=r.Tags}));
        }
        return rows;
    }
    private void Matches_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_rebuilding) SelectionChanged?.Invoke(Selected); }
    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if(!_ready) return;InterruptMatchEndFollow();_pendingRestore=null;ClosePickers();
        var favorites=(CategoryFilter.SelectedItem as CategoryChoice)?.Key=="favorites";
        var previousOwner=FavoritesOnly ? FavoritePlayer?.Id : _archive?.Owner;
        if(favorites && !FavoritesOnly) FavoritePlayer=DefaultFavoritePlayer;
        if(previousOwner!=(favorites ? FavoritePlayer?.Id : _archive?.Owner)) PlayerChanged?.Invoke();
        FavoritesOnly=favorites;Reload();CategoryChanged?.Invoke();
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if(!_ready) return; InterruptMatchEndFollow();_pendingRestore=null; ApplyFilter(); }
    private void Date_Changed(object? sender, EventArgs e) { if(!_ready) return; InterruptMatchEndFollow();_pendingRestore=null; ApplyFilter(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) { InterruptMatchEndFollow();_pendingRestore=null; _searchTimer.Stop(); _searchTimer.Start(); } }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _pendingRestore=null;
        _ready = false; CharacterFilter.SelectedIndex = OpponentCharacterFilter.SelectedIndex = OutcomeFilter.SelectedIndex = 0;
        _filterTags=[];_matchAllTags=false;ClosePickers();RefreshTagFilter();
        DateRange.SetRange(null, null); SearchBox.Text = ""; _searchTimer.Stop(); _ready = true; ApplyFilter();
    }
    private void EditTags_Click(object sender,RoutedEventArgs e)
    {
        if(_archive==null || sender is not Button {DataContext:MatchListRow row} button) return;
        ClosePickers();(_tagPicker ??=new()).Edit(button,_archive.Store,row.Battle,()=>TagsChanged?.Invoke(),FavoritesOnly);e.Handled=true;
    }
    private void Favorites_Click(object sender,RoutedEventArgs e)
    {
        if(_archive==null || !FavoritesOnly) return;ClosePickers();
        (_tagPicker ??=new()).Filter(FavoritesButton,_archive.Store,_filterTags,_matchAllTags,(tags,all)=>
        {_filterTags=tags;_matchAllTags=all;_pendingRestore=null;RefreshTagFilter();ApplyFilter();});
    }
    private void RefreshTagFilter()
    {
        FavoritesButton.Visibility=FavoriteUserButton.Visibility=FavoritesOnly ? Visibility.Visible : Visibility.Collapsed;
        FavoritesButton.Content=_filterTags.Count>0 ? Localization.Format(Strings.SelectedTagsCount, _filterTags.Count) : Strings.AllTagsDropdown;
        FavoritesButton.SetResourceReference(Control.ForegroundProperty,_filterTags.Count>0 ? "AccentText" : "Ink2");
        FavoritesButton.ToolTip=_matchAllTags ? Strings.MatchAllSelectedTags : Strings.MatchAnySelectedTags;
        FavoriteUserButton.Content=new TextBlock {Text=(FavoritePlayer?.DisplayNameFor(_archive?.SignedInId) ?? Strings.Player)+" ▾",TextTrimming=TextTrimming.CharacterEllipsis};
        FavoriteUserButton.ToolTip=FavoritePlayer?.Id;
        SelectedTags.ItemsSource=_archive?.Store.AllTags().Where(t=>_filterTags.Contains(t.Name,StringComparer.OrdinalIgnoreCase)).ToArray();
        SelectedTags.Visibility=FavoritesOnly && _filterTags.Count>0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RemoveTagFilter_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not Button {DataContext:MatchTag tag}) return;
        _filterTags=_filterTags.Where(t=>!string.Equals(t,tag.Name,StringComparison.OrdinalIgnoreCase)).ToArray();
        _pendingRestore=null;RefreshTagFilter();ApplyFilter();
    }
    private void ClosePickers() {if(_tagPicker!=null) _tagPicker.IsOpen=false;if(_playerPicker!=null) _playerPicker.IsOpen=false;}
    private void FavoriteUser_Click(object sender,RoutedEventArgs e)
    {
        if(_archive==null || FavoritePlayer==null) return;
        ClosePickers();
        var signedIn=DefaultFavoritePlayer ?? new PlayerIdentity(_archive.SignedInId ?? _archive.Owner,"");
        (_playerPicker ??=new()).Show(FavoriteUserButton,_favoriteRecords,signedIn,FavoritePlayer.Id,player=>
        {if(FavoritePlayer?.Id==player.Id)return;PlayerChanged?.Invoke();FavoritePlayer=player;_pendingRestore=null;Reload();});
    }
}
