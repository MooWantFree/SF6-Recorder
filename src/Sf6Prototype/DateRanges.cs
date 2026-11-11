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
}
