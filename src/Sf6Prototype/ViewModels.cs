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
public record MatchListRow(Battle Battle, ImageSource? OwnImage, ImageSource? OpponentImage)
{
    public string Result => Battle.Outcome switch { "胜" => "WIN", "负" => "LOSE", "平" => "DRAW", _ => "—" };
    public string ResultColor => Battle.Outcome == "胜" ? "#5B5BD6" : "#85858F";
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

