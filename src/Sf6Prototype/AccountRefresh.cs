namespace Sf6Prototype;

public sealed record AccountRefreshResult(BattlePager Pager,SyncResult Sync);

public static class AccountRefresh
{
    public static async Task<AccountRefreshResult> RunAsync(BattleStore store,IBattleSource source,string accountId,CancellationToken token)
    {
        // Keep the browsing pager intact until all four heads have been merged.
        // Match the normal two-page budget and stop early at a saved boundary.
        var pager=new BattlePager(store,accountId);
        pager.Reset("");
        var result=await pager.LoadInitialAsync(source,"",token,()=>Task.CompletedTask);
        return new(pager,result);
    }
}
