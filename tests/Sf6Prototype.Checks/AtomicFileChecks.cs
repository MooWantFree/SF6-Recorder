using Sf6Prototype;

public static class AtomicFileChecks
{
    public static async Task<int> RunAsync()
    {
        var count=0;
        void Check(bool value,string message) {if(!value)throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var path=Path.Combine(root,"cache.json");var pending=path+".tmp";
        File.WriteAllText(pending,"first");AtomicFile.Commit(pending,path);
        Check(File.ReadAllText(path)=="first" && !File.Exists(pending),"A first cache write commits the complete temporary file");
        File.WriteAllText(pending,"second");
        var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var release=Task.Run(async ()=>{await Task.Delay(60);held.Dispose();});
        try {AtomicFile.Commit(pending,path);} finally {await release;held.Dispose();}
        Check(File.ReadAllText(path)=="second" && !File.Exists(pending),"A transient reader lock can clear before a bounded atomic retry");
        File.WriteAllText(pending,"third");
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            try {AtomicFile.Commit(pending,path);throw new Exception("Expected persistent sharing failure");}
            catch(IOException) {count++;}
            Check(File.ReadAllText(path)=="second" && File.ReadAllText(pending)=="third","A persistent lock preserves both the original and pending contents");
        }
        AtomicFile.Commit(pending,path);
        Check(File.ReadAllText(path)=="third","A failed commit remains recoverable after the lock releases");
        return count;
    }
}
