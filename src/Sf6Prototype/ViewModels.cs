using System.IO;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sf6Prototype;

public static class Pictures
{
    public static ImageSource? Load(string? path, int decodeWidth = 0)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream;
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException) { return null; }
    }
}

/// <summary>Local archive of the currently viewed player, independent of the signed-in account.</summary>
public sealed class Archive(BattleStore store, ProfileCache profiles)
{
    private readonly Dictionary<string, ImageSource?> _images = [];
    private Dictionary<string,Battle> _battles=[];
    private IReadOnlyDictionary<string,string> _scoreLinks=new Dictionary<string,string>();
    private long _loadedRevision=-1;
    private List<Battle>? _nativeRows;
    private Dictionary<string,ScorePoint> _nativePoints=[];
    private Dictionary<string,IReadOnlyList<MatchTag>> _nativeTags=[];
    private sealed record Prepared(string Owner,long Revision,List<Battle> Rows,CurrentScores? Current,
        Dictionary<string,string> Links,Dictionary<string,ScorePoint> Points,Dictionary<string,IReadOnlyList<MatchTag>> Tags);
    private Prepared? _prepared;
    private readonly ScoreProjection _scoreProjection=new();
    public async Task PrepareAsync(string owner,CancellationToken token)
    {
        // Only immutable data is prepared here; WPF image objects and bindings stay on the dispatcher.
        var prepared=await Task.Run(()=>
        {
            token.ThrowIfCancellationRequested();
            var revision=store.ReadRevision;
            var rows=store.QuerySummaries(new(owner));var current=profiles.Scores(owner);
            var links=store.ScoreLinks(owner);var tags=store.TagsFor(owner);
            var points=_scoreProjection.Update(owner,rows,current,links);
            token.ThrowIfCancellationRequested();
            return new Prepared(owner,revision,rows,current,links,points,tags);
        },token);
        if(prepared.Revision==store.ReadRevision) _prepared=prepared;
    }
    public string? SignedInId { get; set; }
    public BattleStore Store => store;
    public ProfileCache Profiles => profiles;
    public string Owner { get; private set; } = "";
    public List<Battle> All { get; private set; } = [];
    public CurrentScores? Current { get; private set; }
    public Dictionary<string, ScorePoint> Points { get; private set; } = [];
    public Dictionary<string,IReadOnlyList<MatchTag>> Tags { get; private set; }=[];
    public void Load(string owner, bool refreshImages = true)
    {
        var current=profiles.Scores(owner);
        var rows=store.QuerySummaries(new(owner));
        var prepared=_prepared is { } p && p.Owner==owner && p.Revision==store.ReadRevision && ReferenceEquals(p.Rows,rows) &&
            SameScores(p.Current,current) ? p : null;
        _prepared=null;
        var changed=Owner!=owner || !ReferenceEquals(rows,_nativeRows) || _loadedRevision!=store.ReadRevision;
        var scoresChanged=!SameScores(Current,current);
        Owner=owner;All=_nativeRows=rows;Current=current;
        if(changed)
        {
            _nativeTags=prepared?.Tags ?? store.TagsFor(owner);
            _scoreLinks=prepared?.Links ?? store.ScoreLinks(owner);_battles=All.ToDictionary(b=>b.ReplayId);
            _loadedRevision=store.ReadRevision;
        }
        if(changed || scoresChanged)
            _nativePoints=prepared?.Points ?? _scoreProjection.Update(owner,All,Current,_scoreLinks);
        Points=_nativePoints;Tags=_nativeTags;
        if (refreshImages) _images.Clear();
    }
    private static bool SameScores(CurrentScores? first,CurrentScores? second) =>
        first?.FetchedAt==second?.FetchedAt && first?.Season==second?.Season &&
        (first?.Characters ?? []).SequenceEqual(second?.Characters ?? []);
    public ImageSource? Image(int id, string? name)
    {
        var path = profiles.CharacterImage(id, name); if (path == null) return null;
        if (!_images.TryGetValue(path, out var value)) _images[path] = value = Pictures.Load(path, 128);
        return value;
    }
    public IReadOnlyList<FavoriteRecord> LoadFavorites(string owner,IReadOnlyList<FavoriteRecord> records)
    {
        Load(owner,false);
        Points=new(Points);
        var favorites=Favorites.ForPlayer(records,owner).Select(r=>r with {Battle=r.Battle.CharacterId<0 ? r.Battle with {CharacterId=profiles.Characters.FirstOrDefault(c=>c.Name==r.Battle.Character)?.Id ?? -1} : r.Battle}).ToArray();
        // Native records retain verified score links. Reversed records have pre-match values only.
        All=All.Concat(favorites.Select(r=>r.Battle)).DistinctBy(b=>b.ReplayId).OrderByDescending(b=>b.PlayedAt).ToList();
        foreach(var record in favorites.Where(r=>r.Battle.Category=="rank" && !Points.ContainsKey(r.Battle.ReplayId)))
        {
            var b=record.Battle;Points[b.ReplayId]=new(b.PlayedAt,b.Time,b.Lp,b.Mr,null,null,b.ReplayId);
        }
        Tags=favorites.ToDictionary(r=>r.Battle.ReplayId,r=>r.Tags);
        return favorites.Select(r=>r with {Battle=All.First(b=>b.ReplayId==r.Battle.ReplayId)}).ToArray();
    }
    public MatchListRow Row(Battle b)
    {
        var point=Points.GetValueOrDefault(b.ReplayId);
        CharacterRank? settledRank=null;
        if(point?.LpAfter!=null)
        {
            if(_scoreLinks.TryGetValue(b.ReplayId,out var nextId) && _battles.TryGetValue(nextId,out var next))
                settledRank=new(b.CharacterId,b.Character,next.Lp,next.Mr,next.LeagueRank,next.MasterLeague,next.MasterRatingRanking);
            else if(point.Estimated) settledRank=Current?.Characters.FirstOrDefault(c=>c.CharacterId==b.CharacterId);
        }
        return new(b,Image(b.CharacterId,b.Character),Image(b.OpponentCharacterId ?? -1,b.OpponentCharacter),point,SignedInId,settledRank,
            profiles.Names.Display(b.CharacterId,b.Character),profiles.Names.Display(b.OpponentCharacterId,b.OpponentCharacter)) {Tags=Tags.GetValueOrDefault(b.ReplayId) ?? []};
    }
}

public sealed class DayGroup(string date) : INotifyPropertyChanged
{
    public string Date { get; } = date;
    private string _summary="";
    public string Summary { get=>_summary; set { if(_summary==value) return; _summary=value; PropertyChanged?.Invoke(this,new(nameof(Summary))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MatchListRow(Battle battle, ImageSource? ownImage, ImageSource? opponentImage, ScorePoint? point,
    string? signedInId=null,CharacterRank? settledRank=null,string? characterName=null,string? opponentCharacterName=null) : INotifyPropertyChanged
{
    public string Character { get; private set; }=characterName ?? battle.Character;
    public string OpponentCharacter { get; private set; }=opponentCharacterName ?? battle.OpponentCharacter;
    public Battle Battle { get; private set; } = battle;
    public ImageSource? OwnImage { get; private set; } = ownImage;
    public ImageSource? OpponentImage { get; private set; } = opponentImage;
    public ScorePoint? Point { get; private set; } = point;
    public string? SignedInId { get; private set; }=signedInId;
    public CharacterRank? SettledRank { get; private set; }=settledRank;
    public IReadOnlyList<MatchTag> Tags { get; internal set; }=[];
    public MatchTag? FirstTag => Tags.FirstOrDefault();
    public string MoreTags => Tags.Count>1 ? $"+{Tags.Count-1}" : "";
    public string TagsTip => Tags.Count==0 ? Strings.AddTags : string.Join("、",Tags.Select(t=>t.Name));
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(MatchListRow row)
    {
        if(Battle==row.Battle && Character==row.Character && OpponentCharacter==row.OpponentCharacter && ReferenceEquals(OwnImage,row.OwnImage) && ReferenceEquals(OpponentImage,row.OpponentImage) && Point==row.Point && SignedInId==row.SignedInId && SettledRank==row.SettledRank && Tags.SequenceEqual(row.Tags)) return;
        Battle=row.Battle; OwnImage=row.OwnImage; OpponentImage=row.OpponentImage; Point=row.Point;
        SignedInId=row.SignedInId; SettledRank=row.SettledRank;
        Tags=row.Tags;
        Character=row.Character;OpponentCharacter=row.OpponentCharacter;
        PropertyChanged?.Invoke(this,new(null));
    }
    public PlayerIdentity Player => new(Battle.OwnerId, Battle.PlayerName);
    public PlayerIdentity OpponentPlayer => new(Battle.OpponentId, Battle.Opponent);
    public string PlayerName => Player.DisplayNameFor(SignedInId);
    public string OpponentName => OpponentPlayer.DisplayNameFor(SignedInId);
    public DayGroup? Group { get; init; }
    public string Kind => Kinds.Of(Battle.Outcome);
    public string Result => Localization.Outcome(Battle.Outcome);
    public IReadOnlyList<RoundResult> Rounds=>RoundResult.From(Battle);
    public bool HasRoundInfo=>Rounds.Count>0;
    public string Clock => Battle.Time.Substring(11, 5);
    public string FullTime => Battle.Time[..16];
    public string DetailTime => BattleDateDisplay.Timestamp(Battle.Time);
    public string Category => BattleCategory.All.FirstOrDefault(c => c.Key == Battle.Category)?.ShortName ?? Battle.Mode;
    public bool IsRanked => Point != null;
    // Prefer MR, unless its settlement is unknown while LP's is known.
    public bool UsesMr => Battle.Mr > 0 && (Point == null || Point.MrDelta != null || Point.LpDelta == null);
    public string RatingUnit => (After ?? Before)<0 ? "" : UsesMr ? "MR" : "LP";
    public int? Delta => UsesMr ? Point?.MrDelta : Point?.LpDelta;
    private int? Before => UsesMr ? Battle.Mr : Battle.Lp;
    private int? After => Point == null ? Before : Before + Delta;
    public bool IsNewChallenger => (After ?? Before)<0;
    public string Rating => ScoreDisplay.Value(After ?? Before);
    public CharacterRank OwnRank => new(Battle.CharacterId,Battle.Character,Battle.Lp,Battle.Mr,Battle.LeagueRank,Battle.MasterLeague,Battle.MasterRatingRanking);
    public CharacterRank OtherRank => new(0,Battle.OpponentCharacter,Battle.OpponentLp,Battle.OpponentMr,Battle.OpponentLeagueRank,Battle.OpponentMasterLeague,Battle.OpponentMasterRatingRanking);
    public CharacterRank RatingRank => SettledRank ?? OwnRank;
    private int? DisplayLp => Point?.LpAfter ?? Battle.Lp;
    public string RankLabel => DisplayLp<0 ? ScoreDisplay.NewChallenger : RatingRank.RankName=="—" ? "" : RatingRank.RankName;
    public string LpSuffix => DisplayLp<0 ? "" : $"{(RankLabel.Length>0 ? " " : "")}({ScoreDisplay.WithUnit(DisplayLp,"LP")})";
    public string RankScore => RankLabel+LpSuffix;
    public string ListLp => DisplayLp<0 ? "" : ScoreDisplay.WithUnit(DisplayLp,"LP");
    public string LpChange => !UsesMr ? ChangeLine : "";
    public string MrChange => UsesMr ? ChangeLine : "";
    public string MasterScore => Battle.Mr>0 ? ScoreDisplay.WithUnit(Point?.MrAfter ?? Battle.Mr,"MR") : "";
    public string RankScoreTip => string.Join("\n",new[]{RankScore,MasterScore,
        Point!=null && Point.LpDelta==null ? Strings.PreMatchLpUnknown : null,
        Point!=null && Battle.Mr>0 && Point.MrDelta==null ? Strings.PreMatchMrUnknown : null,ChangeTip}.Where(s=>!string.IsNullOrEmpty(s)));
    public string? RatingTip => Point != null && Delta == null ? Strings.PreMatchScoreUnknown : null;
    public string Change => Point == null ? "" : Delta?.ToString("+0;−0;0") ?? "—";
    public string ChangeKind => Kinds.Change(Delta);
    public string? ChangeTip => Point?.Estimated == true ? ScoreDisplay.EstimateExplanation : null;
    public string Meta => $"{Battle.Time}  ·  {Battle.Mode}";
    public string OwnLp => ScoreDisplay.WithUnit(Point?.LpAfter ?? Battle.Lp,"LP");
    public string OwnMr => Battle.Mr > 0 ? ScoreDisplay.WithUnit(Point?.MrAfter ?? Battle.Mr,"MR") : "";
    public string OpponentScores => ScoreDisplay.WithUnit(Battle.OpponentLp,"LP") + (Battle.OpponentMr > 0 ? $"  ·  {Battle.OpponentMr:N0} MR" : "");
    public string ChangeLine => Delta == null ? "" : $"{Delta:+0;−0;0} {RatingUnit}";
    public string OwnScoreTip => string.Join("\n",new[] {
        Localization.Format(Strings.BeforeMatch, ScoreDisplay.WithUnit(Battle.Lp,"LP"))+(Battle.Mr>0 ? $" · {ScoreDisplay.WithUnit(Battle.Mr,"MR")}" : ""),
        Point?.LpAfter is { } lp ? Localization.Format(Strings.AfterMatch, ScoreDisplay.WithUnit(lp,"LP")) : Point!=null ? Strings.LpSettlementUnknown : null,
        Point?.MrAfter is { } mr ? Localization.Format(Strings.AfterMatch, ScoreDisplay.WithUnit(mr,"MR")) : Point!=null && Battle.Mr>0 ? Strings.MrSettlementUnknown : null,
        ChangeTip
    }.Where(s=>!string.IsNullOrEmpty(s)));
    public PlayerDetail OwnSide => new(Player,PlayerName,OwnImage,Character,RatingRank,OwnLp,OwnMr,ChangeLine,ChangeKind,OwnScoreTip);
    public PlayerDetail OtherSide => new(OpponentPlayer,OpponentName,OpponentImage,OpponentCharacter,OtherRank,
        ScoreDisplay.WithUnit(Battle.OpponentLp,"LP"),Battle.OpponentMr>0 ? $"{Battle.OpponentMr:N0} MR" : "","","flat",Strings.OfficialMatchScore);
    public string AccessibleName => $"{Battle.Opponent}, {OpponentCharacter}, {Localization.Outcome(Battle.Outcome)} {Battle.Score}, {Battle.Time}";
}

public sealed record PlayerDetail(PlayerIdentity Player,string Name,ImageSource? Image,string Character,CharacterRank Rank,
    string Lp,string Mr,string Change,string ChangeKind,string ScoreTip)
{
    public string Uid => "UID " + Player.Id;
    public string RankLabel => Rank.RankName is "—" or ScoreDisplay.NewChallenger ? "" : Rank.RankName;
    public string LpSuffix => $"{(RankLabel.Length>0 ? " " : "")}{(Lp==ScoreDisplay.NewChallenger ? Lp : $"({Lp})")}";
}

public static class Kinds
{
    public static string Of(string outcome) => outcome switch { "胜" => "win", "负" => "loss", _ => "other" };
    public static string Change(int? delta) => delta > 0 ? "up" : delta < 0 ? "down" : "flat";
}

public record CharacterCard(int Id, string Name, ImageSource? Image, string Rating, string Detail, CharacterRank? Rank)
{
    public string RankLabel => Rank?.RankName is null or "Unranked" or "—" or ScoreDisplay.NewChallenger ? "" : Rank.RankName;
    public string Summary => (RankLabel.Length>0 ? " · " : "")+Detail;
}
public record AccountChoice(string Id, string Name, ImageSource? Image, bool Active);
public record CategoryChoice(string Key, string Name, int Count,bool Incomplete=false)
{
    public string CountText => Count.ToString("N0",Localization.Culture)+(Incomplete ? "*" : "");
    public string? CountTip => Incomplete ? Strings.IncompleteHistoryCount : null;
}
public record OutcomeDot(string Kind, string Tip);

public sealed class HistoryMatchRow(Battle battle, bool isCurrent,CharacterNames? names=null) : INotifyPropertyChanged
{
    public Battle Battle { get; private set; } = battle;
    public bool IsCurrent { get; private set; } = isCurrent;
    public string SelectionStatus => IsCurrent ? Strings.CurrentMatch : "";
    public string Date => BattleDateDisplay.Timestamp(Battle.Time);
    public string Character {get;private set;} = names?.Display(battle.CharacterId,battle.Character) ?? battle.Character;
    public string OpponentCharacter {get;private set;} = names?.Display(battle.OpponentCharacterId,battle.OpponentCharacter) ?? battle.OpponentCharacter;
    public string Score => Battle.Score;
    public string Kind => Kinds.Of(Battle.Outcome);
    public string Description => Localization.Format(Strings.MatchDescription, Battle.Time, Battle.Mode, Character, OpponentCharacter, Localization.Outcome(Battle.Outcome), Score);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(HistoryMatchRow row)
    {
        if(Battle==row.Battle && IsCurrent==row.IsCurrent && Character==row.Character && OpponentCharacter==row.OpponentCharacter) return;
        Battle=row.Battle; IsCurrent=row.IsCurrent;
        Character=row.Character;OpponentCharacter=row.OpponentCharacter;
        PropertyChanged?.Invoke(this,new(null));
    }
}
