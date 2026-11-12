using System.Net;
using System.Net.Http;
using Sf6Prototype;

public static class PlayerSearchChecks
{
    private sealed class Source : IProfileSource
    {
        public int Calls;public string Response="";public Exception? Failure;
        public Task<string> FetchPlayAsync(string owner,CancellationToken token)
        {Calls++;token.ThrowIfCancellationRequested();return Failure!=null ? Task.FromException<string>(Failure) : Task.FromResult(Response);}
        public Task<string> FetchCardAsync(string owner,CancellationToken token)=>throw new Exception("UID lookup should not download portraits");
    }
    public static async Task<int> Run()
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        async Task Fails<T>(Func<Task> action,string message) where T:Exception
        {try{await action();}catch(T){count++;return;}throw new Exception(message);}
        const string id="1234567890";
        var source=new Source {Response="""
            {"pageProps":{"fighter_banner_info":{"personal_info":{"short_id":1234567890,"fighter_id":"玩家甲"}},
            "play":{"current_season_id":14,"season_ids":[13,14],"character_league_infos":[]}}}
            """};
        foreach(var input in new[]{"","123","12345678901","12345a7890","0000000000","１２３４５６７８９０","12345 7890"})
            await Fails<ArgumentException>(()=>PlayerLookup.FindAsync(source,input,CancellationToken.None),"Invalid UIDs must not cause requests");
        Check(source.Calls==0,"Invalid input is rejected entirely locally");
        var player=await PlayerLookup.FindAsync(source,"  "+id+"  ",CancellationToken.None);
        Check(player==new PlayerIdentity(id,"玩家甲") && source.Calls==1,"One profile request validates UID and resolves nickname, including a player with no ranked characters");
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var history=new PlayerSearchHistory(root);history.Remember(player);
        history.Remember(new("9999999999","玩家乙"));history.Remember(player with {Name="新昵称"});
        var reopened=new PlayerSearchHistory(root);
        Check(reopened.Entries.Count==2 && reopened.Entries[0]==player with {Name="新昵称"},"Search history persists both UID and nickname, deduplicates and moves repeated searches to the front");
        for(var i=0;i<25;i++) history.Remember(new((2000000000L+i).ToString(),"玩家"+i));
        Check(new PlayerSearchHistory(root).Entries.Count==20,"Search history keeps a bounded recent list");
        source.Response="{\"notFound\":true}";
        await Fails<InvalidDataException>(()=>PlayerLookup.FindAsync(source,id,CancellationToken.None),"An unavailable profile is not treated as an empty valid player");
        source.Failure=new HttpRequestException("missing",null,HttpStatusCode.NotFound);
        await Fails<InvalidDataException>(()=>PlayerLookup.FindAsync(source,id,CancellationToken.None),"HTTP 404 has a player-not-found error");
        source.Failure=new LoginRequiredException("expired");
        await Fails<LoginRequiredException>(()=>PlayerLookup.FindAsync(source,id,CancellationToken.None),"Expired login remains distinguishable from an invalid UID");
        source.Failure=null;source.Response="{\"pageProps\":{}}";
        await Fails<InvalidDataException>(()=>PlayerLookup.FindAsync(source,id,CancellationToken.None),"Unexpected response shape cannot navigate to an unverified player");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Fails<OperationCanceledException>(()=>PlayerLookup.FindAsync(source,id,cancelled.Token),"Cancelled lookups cannot resolve a stale navigation target");
        Check(new PlayerSearchHistory(root).Entries.SequenceEqual(history.Entries),"Failed lookups do not add or reorder search history");
        File.WriteAllText(Path.Combine(root,"player-search.json"),"broken");
        Check(new PlayerSearchHistory(root).Entries.Count==0,"A damaged history file does not prevent startup or lookup");
        return count;
    }
}
