using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Sf6Prototype;

public sealed class RankColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is CharacterRank { Ranked: true } rank ? rank.RankName switch
        {
            "Legend" => "RankLegend",
            "Master" => "RankMaster",
            "High Master" => "RankHighMaster",
            "Grand Master" => "RankGrandMaster",
            "Ultimate Master" => "RankUltimateMaster",
            _ => rank.ResolvedLeagueRank switch
            {
                >= 1 and <= 5 => "RankRookie",
                >= 6 and <= 10 => "RankIron",
                >= 11 and <= 15 => "RankBronze",
                >= 16 and <= 20 => "RankSilver",
                >= 21 and <= 25 => "RankGold",
                >= 26 and <= 30 => "RankPlatinum",
                >= 31 and <= 35 => "RankDiamond",
                _ => "Ink2"
            }
        } : "Ink2";
        return Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
