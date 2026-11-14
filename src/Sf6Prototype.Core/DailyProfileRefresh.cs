namespace Sf6Prototype;

// One successful profile update per account and local day, with a 06:00 boundary.
// The captured start time keeps a refresh crossing 06:00 in its original day.
public sealed class DailyProfileRefresh(ProfileCache profiles,string owner,DateTimeOffset startedAt,TimeZoneInfo? timeZone=null)
{
    public bool Required {get;}=IsDue(profiles.Profile(owner)?.DailyRefreshedAt,startedAt,timeZone ?? TimeZoneInfo.Local);
    private bool _avatarUpdated;
    public static DateOnly Day(DateTimeOffset at,TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at,timeZone).DateTime.AddHours(-6));
    public static bool IsDue(DateTimeOffset? previous,DateTimeOffset at,TimeZoneInfo timeZone) =>
        previous==null || Day(previous.Value,timeZone)<Day(at,timeZone);
    public async Task<IReadOnlyList<ImageDownload>> RefreshAvatarAsync(IProfileSource source,CancellationToken token)
    {
        if(!Required) return [];
        var images=await profiles.RefreshAvatarAsync(source,owner,token);
        _avatarUpdated=true;return images;
    }
    public async Task<IReadOnlyList<ImageDownload>> RefreshPlayAsync(IProfileSource source,CancellationToken token,bool captureScores=false)
    {
        if(!Required) return [];
        var images=await profiles.RefreshPlayAsync(source,owner,token,captureScores);
        token.ThrowIfCancellationRequested();
        if(_avatarUpdated) profiles.CompleteDailyRefresh(owner,startedAt);
        return images;
    }
}
