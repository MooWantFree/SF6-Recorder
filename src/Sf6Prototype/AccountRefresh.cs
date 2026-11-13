namespace Sf6Prototype;

public sealed record AccountRefreshResult(BattlePager Pager,SyncResult Sync);

public static class AccountRefresh
{
    public static async Task<AccountRefreshResult> RunAsync(BattleStore store,IBattleSource source,string accountId,CancellationToken token)
    {
        var pager=new BattlePager(store,accountId);
        var result=await pager.LoadAccountAsync(source,token,()=>Task.CompletedTask);
        return new(pager,result);
    }
}
