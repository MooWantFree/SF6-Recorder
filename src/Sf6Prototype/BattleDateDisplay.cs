using System.Globalization;

namespace Sf6Prototype;

public static class BattleDateDisplay
{
    public static int CurrentYear => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Year;
    public static string Timestamp(string time,int? currentYear=null) =>
        int.TryParse(time.AsSpan(0,4),NumberStyles.None,CultureInfo.InvariantCulture,out var year) && year==(currentYear ?? CurrentYear)
            ? time[5..16] : time[..16];
}
