namespace Sf6Prototype;

public sealed record PlayerIdentity(string Id, string Name)
{
    public bool IsValid => Id is {Length:10} && Id.All(char.IsAsciiDigit) && Id.Any(c=>c!='0');
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
    public string DisplayNameFor(string? signedInId) => DisplayName + (IsValid && Id==signedInId ? Strings.MeSuffix : "");
}

public sealed record HistoryScrollAnchor(string ReplayId,double Top);
public sealed record HistoryViewState
{
    public string Category { get; init; }="";
    public string? Character { get; init; }
    public string? OpponentCharacter { get; init; }
    public bool FavoritesOnly { get; init; }
    public PlayerIdentity? FavoritePlayer { get; init; }
    public IReadOnlyList<string> Tags { get; init; }=[];
    public bool MatchAllTags { get; init; }
    public string? Outcome { get; init; }
    public string Search { get; init; }="";
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public string? SelectedReplay { get; init; }
    public HistoryScrollAnchor? Anchor { get; init; }
    public double ScrollOffset { get; init; }
    public IReadOnlyList<string> VisibleReplays { get; init; }=[];
}
public sealed record ScoresViewState(int? CharacterId=null,bool ShowMr=false,int Count=20,string? SelectedReplay=null,DateTime? Start=null,DateTime? End=null);
public sealed record PlayerViewState
{
    public HistoryViewState History { get; init; }=new();
    public ScoresViewState Scores { get; init; }=new();
    public int PageIndex { get; init; }
    public string? DetailReplay { get; init; }
}

public sealed record NavigationDestination(PlayerIdentity Player,PlayerViewState View,int Steps)
{
    public string Title => View.PageIndex==1 ? Localization.Format(Strings.ScoreNavigationTitle, Player.DisplayName, (View.Scores.ShowMr ? "MR" : "LP")) :
        View.History.FavoritesOnly ? Localization.Format(Strings.FavoritesNavigationTitle, (View.History.FavoritePlayer ?? Player).DisplayName) :
        Localization.Format(Strings.HistoryNavigationTitle, Player.DisplayName, BattleCategory.All.FirstOrDefault(c=>c.Key==View.History.Category)?.ShortName ?? Strings.All);
}

// Browsing a public player profile must never change the signed-in account.
public sealed class PlayerNavigation
{
    private sealed record Entry(PlayerIdentity Player,PlayerViewState View);
    private readonly Stack<Entry> _back = [];
    private readonly Stack<Entry> _forward = [];
    private PlayerViewState _homeView=new();
    public PlayerIdentity Home { get; private set; } = new("", "");
    public PlayerIdentity Current { get; private set; } = new("", "");
    public PlayerViewState View { get; private set; }=new();
    public string Category => View.History.Category;
    public bool IsHome => Current.Id == Home.Id;
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public IReadOnlyList<NavigationDestination> BackHistory => Destinations(_back);
    public IReadOnlyList<NavigationDestination> ForwardHistory => Destinations(_forward);
    private IReadOnlyList<NavigationDestination> Destinations(Stack<Entry> stack) => stack.Select((e,i)=>new NavigationDestination(e.Player.Id==Home.Id ? Home : e.Player,e.View,i+1)).ToList();

    public void SetHome(PlayerIdentity home, bool preserveView = false)
    {
        if (!home.IsValid) throw new ArgumentException(Strings.InvalidPlayerUid);
        var wasHome = IsHome;
        var sameAccount = Home.Id==home.Id;
        Home = home;
        if (!preserveView || !sameAccount || !Current.IsValid)
        { _homeView=new();_back.Clear();_forward.Clear();Current=Home;View=_homeView; }
        else if(wasHome) Current=home;
    }
    public void SelectCategory(string category)
    {
        if(category!="" && !BattleCategory.All.Any(c=>c.Key==category)) throw new ArgumentException(Strings.UnknownCategory);
        RememberView(View with {History=View.History with {Category=category}});
    }
    public void RememberView(PlayerViewState view)
    {
        // Snapshot the loaded rows too: later paging must not mutate a back entry.
        View=view with {History=view.History with {VisibleReplays=view.History.VisibleReplays.ToArray(),Tags=view.History.Tags.ToArray()}};
        if(IsHome) _homeView=View;
    }
    public bool Visit(PlayerIdentity player)
    {
        if (!player.IsValid) throw new ArgumentException(Strings.InvalidPlayerUid);
        if (player.Id == Current.Id) return false;
        if (Current.IsValid) _back.Push(new(Current,View));
        _forward.Clear();
        Current = player; View=new() {PageIndex=View.PageIndex};
        return true;
    }
    public void Back(int steps=1) => Traverse(_back,_forward,steps);
    public void Forward(int steps=1) => Traverse(_forward,_back,steps);
    private void Traverse(Stack<Entry> source,Stack<Entry> destination,int steps)
    {
        if(steps<1) throw new ArgumentOutOfRangeException(nameof(steps));
        for(var i=0;i<steps && source.TryPop(out var previous);i++)
        {
            destination.Push(new(Current,View));
            Current = previous.Player.Id == Home.Id ? Home : previous.Player;
            RememberView(previous.View);
        }
    }
    public void ReturnHome()
    {
        if(IsHome) return;
        if(Current.IsValid) _back.Push(new(Current,View));
        _forward.Clear();Current=Home;View=_homeView;
    }
    public void UpdateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Current = Current with { Name = name };
        if (IsHome) Home = Current;
    }
}
