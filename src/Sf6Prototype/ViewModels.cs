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
        Owner = owner; All = store.Query(new(owner)); Current = profiles.Scores(owner);
        Tags=store.TagsFor(owner);
        _scoreLinks=store.ScoreLinks(owner); _battles=All.ToDictionary(b=>b.ReplayId);
        Points = BattleStore.Scores(All, Current, links: _scoreLinks).ToDictionary(p => p.ReplayId);
        if (refreshImages) _images.Clear();
    }
    public ImageSource? Image(int id, string? name)
    {
        var path = profiles.CharacterImage(id, name); if (path == null) return null;
        if (!_images.TryGetValue(path, out var value)) _images[path] = value = Pictures.Load(path, 128);
        return value;
    }
    public IReadOnlyList<FavoriteRecord> LoadFavorites(string owner,IReadOnlyList<FavoriteRecord> records)
    {
        Load(owner,false);
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
        return new(b,Image(b.CharacterId,b.Character),Image(-1,b.OpponentCharacter),point,SignedInId,settledRank) {Tags=Tags.GetValueOrDefault(b.ReplayId) ?? []};
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
    string? signedInId=null,CharacterRank? settledRank=null) : INotifyPropertyChanged
{
    public Battle Battle { get; private set; } = battle;
    public ImageSource? OwnImage { get; private set; } = ownImage;
    public ImageSource? OpponentImage { get; private set; } = opponentImage;
    public ScorePoint? Point { get; private set; } = point;
    public string? SignedInId { get; private set; }=signedInId;
    public CharacterRank? SettledRank { get; private set; }=settledRank;
    public IReadOnlyList<MatchTag> Tags { get; internal set; }=[];
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(MatchListRow row)
    {
        if(Battle==row.Battle && ReferenceEquals(OwnImage,row.OwnImage) && ReferenceEquals(OpponentImage,row.OpponentImage) && Point==row.Point && SignedInId==row.SignedInId && SettledRank==row.SettledRank && Tags.SequenceEqual(row.Tags)) return;
        Battle=row.Battle; OwnImage=row.OwnImage; OpponentImage=row.OpponentImage; Point=row.Point;
        SignedInId=row.SignedInId; SettledRank=row.SettledRank;
        Tags=row.Tags;
        PropertyChanged?.Invoke(this,new(null));
    }
    public PlayerIdentity Player => new(Battle.OwnerId, Battle.PlayerName);
    public PlayerIdentity OpponentPlayer => new(Battle.OpponentId, Battle.Opponent);
    public string PlayerName => Player.DisplayNameFor(SignedInId);
    public string OpponentName => OpponentPlayer.DisplayNameFor(SignedInId);
    public DayGroup? Group { get; init; }
    public string Kind => Kinds.Of(Battle.Outcome);
    public string Result => Battle.Outcome;
    public string Clock => Battle.Time.Substring(11, 5);
    public string FullTime => Battle.Time[..16].Replace(' ','-').Replace(':','-');
    public string DetailTime => Battle.Time[5..16];
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
    public string LpSuffix => DisplayLp<0 ? "" : $"{(RankLabel.Length>0 ? " " : "")}({DisplayLp?.ToString("0") ?? "—"}lp)";
    public string RankScore => RankLabel+LpSuffix;
    public string MasterScore => Battle.Mr>0 ? $"{Point?.MrAfter ?? Battle.Mr:0}mr" : "";
    public string RankScoreTip => string.Join("\n",new[]{RankScore,MasterScore,
        Point!=null && Point.LpDelta==null ? "LP 为赛前分数，本场结算未知" : null,
        Point!=null && Battle.Mr>0 && Point.MrDelta==null ? "MR 为赛前分数，本场结算未知" : null,ChangeTip}.Where(s=>!string.IsNullOrEmpty(s)));
    public string? RatingTip => Point != null && Delta == null ? "赛前分数，本场结算未知" : null;
    public string Change => Point == null ? "" : Delta?.ToString("+0;−0;0") ?? "—";
    public string ChangeKind => Kinds.Change(Delta);
    public string? ChangeTip => Point?.Estimated == true ? ScoreDisplay.EstimateExplanation : null;
    public string Meta => $"{Battle.Time}  ·  {Battle.Mode}";
    public string OwnLp => ScoreDisplay.WithUnit(Point?.LpAfter ?? Battle.Lp,"LP");
    public string OwnMr => Battle.Mr > 0 ? ScoreDisplay.WithUnit(Point?.MrAfter ?? Battle.Mr,"MR") : "";
    public string OpponentScores => ScoreDisplay.WithUnit(Battle.OpponentLp,"LP") + (Battle.OpponentMr > 0 ? $"  ·  {Battle.OpponentMr:N0} MR" : "");
    public string ChangeLine => Delta == null ? "" : $"{Delta:+0;−0;0} {RatingUnit}";
    public string OwnScoreTip => string.Join("\n",new[] {
        $"赛前：{ScoreDisplay.WithUnit(Battle.Lp,"LP")}"+(Battle.Mr>0 ? $" · {ScoreDisplay.WithUnit(Battle.Mr,"MR")}" : ""),
        Point?.LpAfter is { } lp ? $"赛后：{ScoreDisplay.WithUnit(lp,"LP")}" : Point!=null ? "本场 LP 结算未知" : null,
        Point?.MrAfter is { } mr ? $"赛后：{ScoreDisplay.WithUnit(mr,"MR")}" : Point!=null && Battle.Mr>0 ? "本场 MR 结算未知" : null,
        ChangeTip
    }.Where(s=>!string.IsNullOrEmpty(s)));
    public PlayerDetail OwnSide => new(Player,PlayerName,OwnImage,Battle.Character,RatingRank,OwnLp,OwnMr,ChangeLine,ChangeKind,OwnScoreTip);
    public PlayerDetail OtherSide => new(OpponentPlayer,OpponentName,OpponentImage,Battle.OpponentCharacter,OtherRank,
        ScoreDisplay.WithUnit(Battle.OpponentLp,"LP"),Battle.OpponentMr>0 ? $"{Battle.OpponentMr:N0} MR" : "","","flat","官网记录的对局时分数");
    public string AccessibleName => $"{Battle.Opponent}，{Battle.OpponentCharacter}，{Battle.Outcome} {Battle.Score}，{Battle.Time}";
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
    public string RankLabel => Rank?.RankName is null or "未定级" or "—" or ScoreDisplay.NewChallenger ? "" : Rank.RankName;
    public string Summary => (RankLabel.Length>0 ? " · " : "")+Detail;
}
public record AccountChoice(string Id, string Name, ImageSource? Image, bool Active);
public record CategoryChoice(string Key, string Name, int Count);
public record OutcomeDot(string Kind, string Tip);

public sealed class HistoryMatchRow(Battle battle, bool isCurrent) : INotifyPropertyChanged
{
    public Battle Battle { get; private set; } = battle;
    public bool IsCurrent { get; private set; } = isCurrent;
    public string SelectionStatus => IsCurrent ? "当前对局" : "";
    public string Date => Battle.Time.Substring(5,11).Replace('-','/');
    public string Character => Battle.Character;
    public string OpponentCharacter => Battle.OpponentCharacter;
    public string Score => Battle.Score;
    public string Kind => Kinds.Of(Battle.Outcome);
    public string Description => $"{Battle.Time} · {Battle.Mode} · {Character} 对 {OpponentCharacter} · {Battle.Outcome} {Score}";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(HistoryMatchRow row)
    {
        if(Battle==row.Battle && IsCurrent==row.IsCurrent) return;
        Battle=row.Battle; IsCurrent=row.IsCurrent;
        PropertyChanged?.Invoke(this,new(null));
    }
}
