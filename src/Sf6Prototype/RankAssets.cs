using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sf6Prototype;

public static class RankAssets
{
    private static readonly Dictionary<int,ImageSource?> Images=[];
    public static ImageSource? Image(CharacterRank rank)
    {
        if(rank.BadgeId is not { } id) return null;
        if(Images.TryGetValue(id,out var cached)) return cached;
        try
        {
            var image=new BitmapImage(new Uri($"pack://application:,,,/Assets/Ranks/rank{id}_s.png",UriKind.Absolute));
            image.Freeze(); return Images[id]=image;
        }
        catch(Exception ex) when(ex is System.IO.IOException or NotSupportedException) { return Images[id]=null; }
    }
}
