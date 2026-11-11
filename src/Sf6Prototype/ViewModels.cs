using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sf6Prototype;

public static class Pictures
{
    public static ImageSource? Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException) { return null; }
    }
}
public record MatchListRow(Battle Battle, ImageSource? OwnImage, ImageSource? OpponentImage, ScorePoint? Point)
{
    public string Result => Battle.Outcome;
    public string ResultColor => Battle.Outcome == "胜" ? "#5959BF" : "#707079";
    public string ResultBackground => Battle.Outcome == "胜" ? "#EFEFFB" : "#F0F0F3";
    public string DateGroup => DateTime.ParseExact(Battle.Time[..10],"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture).ToString("yyyy年M月d日  dddd",System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
    public string Clock => Battle.Time.Substring(11,5);
    public string Category => BattleCategory.All.FirstOrDefault(c=>c.Key==Battle.Category)?.ShortName ?? Battle.Mode;
    public string Rating => Battle.Mr > 0 ? Battle.MrText : Battle.LpText;
    public string RatingUnit => Battle.Mr > 0 ? "MR" : "LP";
    public int? Delta => Battle.Mr > 0 ? Point?.MrDelta : Point?.LpDelta;
    public string Change => Delta?.ToString("+0;-0;0") ?? "—";
    public string ChangeColor => Delta > 0 ? "#5959BF" : Delta < 0 ? "#92616B" : "#898992";
    public string AccessibleName => $"{Battle.Character} 对 {Battle.Opponent}，{Battle.Outcome}，{Battle.Time}";
    public string Meta => $"{Battle.Time}  ·  {Battle.Mode}";
    public string OwnPoints => $"{Battle.Character}  ·  {Battle.LpText} LP" + (Battle.Mr > 0 ? $"  /  {Battle.MrText} MR" : "");
    public string OpponentPoints => $"{Battle.OpponentCharacter}  ·  {Battle.OpponentLp?.ToString("N0") ?? "—"} LP" + (Battle.OpponentMr > 0 ? $"  /  {Battle.OpponentMr:N0} MR" : "");
}
public record CharacterChoice(CharacterSummary Summary, ImageSource? Image)
{
    public string Name => Summary.Character;
    public int Id => Summary.CharacterId;
}
public record AccountChoice(string Id, string Name, ImageSource? Image, bool Active);

