namespace Sf6Prototype;

public static class DateRanges
{
    public static (DateTime? Start, DateTime? End) Preset(string key, DateTime today)
    {
        today = today.Date;
        var first = new DateTime(today.Year, today.Month, 1);
        return key switch
        {
            "yesterday" => (today.AddDays(-1), today.AddDays(-1)),
            "month" => (first, today),
            "previous" => (first.AddMonths(-1), first.AddDays(-1)),
            "90" => (today.AddDays(-89), today),
            _ => (null, null)
        };
    }
    public static (DateTime Start, DateTime End) Normalize(DateTime first, DateTime last) => first <= last ? (first.Date,last.Date) : (last.Date,first.Date);
    public static IReadOnlyList<ScorePoint> Recent(IEnumerable<ScorePoint> points, int count) => points.OrderBy(p => p.PlayedAt).ThenBy(p => p.ReplayId).TakeLast(count <= 0 ? int.MaxValue : count).ToList();
    public static int? ScoreDifference(IReadOnlyList<ScorePoint> points, bool mr)
    {
        if (points.Count < 2) return null;
        if (!mr) return points[^1].Lp - points[0].Lp;
        // A missing MR delta identifies invalid MR, unknown seasons, or a season boundary.
        if (points.Any(p=>p.Mr is null or <=0) || points.Skip(1).Any(p=>p.MrDelta==null)) return null;
        return points[^1].Mr - points[0].Mr;
    }
}
