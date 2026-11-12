namespace Sf6Prototype;

public static class DateRanges
{
    public static long? Epoch(DateTime? date) => date==null ? null : new DateTimeOffset(DateTime.SpecifyKind(date.Value.Date,DateTimeKind.Unspecified),TimeSpan.FromHours(8)).ToUnixTimeSeconds();
    public static IReadOnlyList<int> Extrema(IReadOnlyList<double> values)
    {
        if(values.Count==0) return [];
        var low=0;var high=0;
        for(var i=1;i<values.Count;i++) {if(values[i]<=values[low]) low=i;if(values[i]>=values[high]) high=i;}
        return low==high ? [high] : [high,low];
    }
    public static bool IsRefreshOverdue(DateTimeOffset? lastUpdated,DateTimeOffset now,int seconds=ConnectionSettings.DefaultRefreshReminderSeconds) =>
        seconds>0 && lastUpdated!=null && now-lastUpdated.Value>TimeSpan.FromSeconds(seconds);
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
        if (points.Count == 0) return null;
        // A missing MR delta identifies invalid MR, unknown seasons, or a season boundary.
        if (mr) return points.Any(p=>p.MrDelta==null) ? null : points.Sum(p=>p.MrDelta);
        return points.All(p=>p.LpDelta==null) ? null : points.Sum(p=>p.LpDelta ?? 0);
    }
}
