using Sf6Prototype;

public static class ScoreProjectionChecks
{
    public static int Run()
    {
        var count=0;void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
        var projection=new ScoreProjection();var rows=new List<Battle>();var links=new Dictionary<string,string>();
        CurrentScores current=new(DateTimeOffset.FromUnixTimeSeconds(10000),1,[new(1,"RYU",16000,1800,36,null)]);
        void Equivalent(string owner)
        {
            var actual=projection.Update(owner,rows,current,links);
            var expected=ScoreCalculator.Calculate(rows,current,links:links).ToDictionary(p=>p.ReplayId);
            Check(actual.Count==expected.Count && expected.All(p=>actual[p.Key]==p.Value),"Incremental settlements equal the complete calculation across inserts, gaps and profile changes");
        }
        for(var batch=0;batch<12;batch++)
        {
            for(var n=0;n<17;n++)
            {
                var i=batch*17+n;
                rows.Add(new(){OwnerId="1234567890",ReplayId=(i%2==0 ? "a-" : "Z_")+i,Category=i%7==0 ? "casual" : "rank",PlayedAt=1000+i/3,
                    CharacterId=i%3+1,Character="fixture",Lp=12000+i*10,Mr=1500+i,Season=i<100 ? 1 : 2,MyRounds=2,OpponentRounds=1});
            }
            links.Clear();
            foreach(var group in rows.Where(b=>b.Category=="rank").GroupBy(b=>b.CharacterId))
            {
                var ordered=group.OrderBy(b=>b.PlayedAt).ThenBy(b=>b.ReplayId).ToArray();
                for(var i=0;i+1<ordered.Length;i++)if(i%4!=0)links[ordered[i].ReplayId]=ordered[i+1].ReplayId;
            }
            Equivalent("1234567890");
            var before=projection.Update("1234567890",rows,current,links);
            Check(ReferenceEquals(before,projection.Update("1234567890",rows,current,links)),"Unchanged score evidence reuses the result dictionary");
        }
        var stable=projection.Update("1234567890",rows,current,links);var old=rows.First(b=>b.Category=="rank" && links.ContainsKey(b.ReplayId));
        current=current with {FetchedAt=current.FetchedAt.AddSeconds(1),Season=2,Characters=[new(1,"RYU",17000,1900,40,null)]};
        Equivalent("1234567890");
        Check(ReferenceEquals(stable[old.ReplayId],projection.Update("1234567890",rows,current,links)[old.ReplayId]),"Updating a profile reuses verified historical settlements");
        rows.RemoveAt(20);Equivalent("1234567890");
        rows[0]=rows[0] with {Category="rank",Lp=13000,PlayedAt=2000};Equivalent("1234567890");
        rows=rows.Select(b=>b with {OwnerId="2222222222",Lp=b.Lp+100}).ToList();Equivalent("2222222222");
        return count;
    }
}
