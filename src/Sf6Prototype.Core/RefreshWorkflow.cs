using System.Net;
using System.Net.Http;

namespace Sf6Prototype;

public interface IRefreshSource : IBattleSource, IProfileSource
{
    string AccountId { get; }
    event Action<RequestActivity>? Activity;
    Task ValidateAsync(CancellationToken token);
}
public enum RefreshMode { Account, Initial, More, Scores }
public sealed record RefreshPlan(BattlePager Pager, RefreshMode Mode, string Category = "", ScoreRange? Range = null,
    bool ForceHead = false, IReadOnlyList<Battle>? Filtered = null, DailyProfileRefresh? Profile = null);
public sealed record RefreshResult(SyncResult Sync, bool ScoresPending);
public sealed class RefreshCallbacks
{
    public Action BeforeValidation { get; init; } = () => { };
    public Func<Task> PrepareImages { get; init; } = () => Task.CompletedTask;
    public Action<IReadOnlyList<ImageDownload>> ImagesReady { get; init; } = _ => { };
    public Action AvatarUpdated { get; init; } = () => { };
    public Func<Task> PageSaved { get; init; } = () => Task.CompletedTask;
    public Func<string?>? PreferredCategory { get; init; }
    public Action BattlesLoaded { get; init; } = () => { };
    public Action BeforeProfile { get; init; } = () => { };
    public Action<BattleLoadProgress,int> Progress { get; init; } = (_,_) => { };
    public Action<RequestActivity> Request { get; init; } = _ => { };
}

// Shared refresh use case. Views supply presentation callbacks and the post-validation
// account selection; ordering, metadata policy, paging and cleanup live here.
public static class RefreshWorkflow
{
    public static async Task<RefreshResult> RunAsync(IRefreshSource source, bool validate,
        Func<Task<RefreshPlan>> prepare, RefreshCallbacks callbacks, bool automatic, CancellationToken token)
    {
        source.Activity += callbacks.Request;
        BattlePager? pager = null;
        DailyProfileRefresh? daily = null;
        bool NeedsProfile() => daily != null && (daily.Required || pager?.RankHeadFetched == true);
        void Progress(BattleLoadProgress progress) => callbacks.Progress(progress, NeedsProfile() ? 1 : 0);
        try
        {
            token.ThrowIfCancellationRequested();
            if (validate) { callbacks.BeforeValidation(); await source.ValidateAsync(token); }
            token.ThrowIfCancellationRequested();
            var plan = await prepare(); pager = plan.Pager; daily = plan.Profile;
            pager.ProgressChanged += Progress;
            var scoresPending = false;
            if (daily?.Required == true)
            {
                await callbacks.PrepareImages();
                token.ThrowIfCancellationRequested();
                try
                {
                    callbacks.ImagesReady(await daily.RefreshAvatarAsync(source, token));
                    callbacks.AvatarUpdated();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException and not LoginRequiredException
                    && ex is not HttpRequestException {StatusCode:HttpStatusCode.TooManyRequests})
                { Diagnostics.Write("sync", "refresh_avatar", ex); }
            }
            var result = plan.Mode switch
            {
                RefreshMode.Account => await pager.LoadAccountAsync(source,token,callbacks.PageSaved,callbacks.PreferredCategory),
                RefreshMode.Scores => await pager.LoadScoreRangeAsync(source,plan.Range!,token,callbacks.PageSaved,plan.ForceHead),
                RefreshMode.More => await pager.LoadMoreAsync(source,plan.Category,plan.Filtered!,token,callbacks.PageSaved),
                _ => await pager.LoadInitialAsync(source,plan.Category,token,callbacks.PageSaved)
            };
            token.ThrowIfCancellationRequested();
            callbacks.BattlesLoaded();
            try
            {
                if (NeedsProfile())
                {
                    callbacks.BeforeProfile();
                    var images = await daily!.RefreshPlayAsync(source,token,captureScores:pager.RankHeadFetched);
                    token.ThrowIfCancellationRequested(); callbacks.ImagesReady(images);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException && (!automatic || ex is not LoginRequiredException))
            { Diagnostics.Write("sync", "refresh_profile", ex); scoresPending = pager.RankHeadFetched; }
            return new(result with {Requests=source.Requests}, scoresPending);
        }
        finally
        {
            source.Activity -= callbacks.Request;
            if (pager != null) pager.ProgressChanged -= Progress;
        }
    }
}
