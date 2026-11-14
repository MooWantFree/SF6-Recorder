using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sf6Prototype;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;

public static class GitHubUpdateChecks
{
    public static async Task<int> Run()
    {
        var count=0;
        void Check(bool ok,string message) {if(!ok)throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks","updates-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var payload=Encoding.UTF8.GetBytes("a fake update payload");
        var hash=Convert.ToHexString(SHA256.HashData(payload));
        string Feed(string version="0.0.3",string id=GitHubUpdates.PackageId,string? file=null,string? sha=null,
            string channel=GitHubUpdates.SelfContainedChannel) =>
            JsonSerializer.Serialize(new {Assets=new[]{new {PackageId=id,Version=version,Type="Full",
                FileName=file ?? $"SF6Recorder-{version}{(channel==GitHubUpdates.SelfContainedChannel ? "" : "-"+channel)}-full.nupkg",SHA256=sha??hash,Size=payload.Length}}});
        TestVelopackLocator Locator(string current="0.0.2",string? channel=GitHubUpdates.SelfContainedChannel)
        {
            var directory=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            return new(GitHubUpdates.PackageId,current,directory,Path.Combine(directory,"current"),directory,Path.Combine(directory,"Update.exe"),channel);
        }
        var requests=new List<Uri>();
        HttpClient Client(string feed,HttpStatusCode feedStatus=HttpStatusCode.OK,bool headUnsupported=false) => new(new ReplyHandler((request,_)=>
        {
            requests.Add(request.RequestUri!);
            Check(request.RequestUri!.Host=="github.com" && request.Headers.Authorization==null,"Release checks and downloads never use REST API or account credentials");
            if(request.RequestUri.AbsolutePath.EndsWith("/latest"))
            {
                if(headUnsupported && request.Method==HttpMethod.Head) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {RequestMessage=new(HttpMethod.Get,GitHubUpdates.RepositoryUrl+"/releases/tag/v0.0.3")});
            }
            if(request.RequestUri.AbsolutePath.EndsWith(".json"))
                return Task.FromResult(new HttpResponseMessage(feedStatus) {Content=new StringContent(feed)});
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new ByteArrayContent(payload)});
        }));
        foreach(var (version,current,expected) in new[]{("0.0.3","0.0.2",UpdateStatus.Available),("0.0.2","0.0.2",UpdateStatus.UpToDate),("0.0.9","0.0.10",UpdateStatus.UpToDate),("1.0.0","0.0.2",UpdateStatus.Available)})
        {
            using var client=Client(Feed(version));
            var result=await GitHubUpdates.CheckAsync(client,Locator(current),CancellationToken.None);
            Check(result.Status==expected,"Velopack compares numeric installed and feed versions");
        }
        using(var client=Client(Feed(),headUnsupported:true))
            Check((await GitHubUpdates.CheckAsync(client,Locator(),CancellationToken.None)).Status==UpdateStatus.Available,"HEAD fallback reads GET response headers");
        using(var client=Client("",HttpStatusCode.NotFound))
            Check((await GitHubUpdates.CheckAsync(client,Locator(),CancellationToken.None)).Status==UpdateStatus.NoRelease,"Old GitHub releases without update assets do not advertise an installable update");
        foreach(var bad in new[]{"{","{}",Feed(id:"AnotherApp"),Feed(file:"../danger-full.nupkg"),Feed(sha:"invalid"),Feed(version:"0.0.3-beta")})
        {
            using var client=Client(bad);
            try {await GitHubUpdates.CheckAsync(client,Locator(),CancellationToken.None);throw new Exception("Invalid feed accepted");}
            catch(Exception ex) when(ex is JsonException or InvalidDataException) {count++;}
        }
        using(var client=Client(Feed(),HttpStatusCode.TooManyRequests))
        {
            try {await GitHubUpdates.CheckAsync(client,Locator(),CancellationToken.None);throw new Exception("HTTP failure accepted");}
            catch(HttpRequestException ex) {Check(ex.StatusCode==HttpStatusCode.TooManyRequests,"Transport errors are not no-update results");}
        }
        using(var client=Client(Feed()))
        {
            var locator=Locator();var result=await GitHubUpdates.CheckAsync(client,locator,CancellationToken.None);var release=result.Release!;
            Check(release.Page.AbsoluteUri.EndsWith("/tag/v0.0.3"),"Release page identifies the checked tag");
            requests.Clear();
            var source=new GitHubReleaseSource(client,CancellationToken.None,release.Tag);
            var file=Path.Combine(locator.PackagesDir!,release.Package.TargetFullRelease.FileName);
            await source.DownloadReleaseEntry(new NullVelopackLogger(),release.Package.TargetFullRelease,file,_=>{});
            Check(requests.Count==1 && requests[0].AbsolutePath.Contains("/download/v0.0.3/"),"Downloads remain pinned and never ask Latest again");
            await GitHubUpdates.VerifyAsync(release,locator,CancellationToken.None);count++;
            File.WriteAllBytes(file,Encoding.UTF8.GetBytes("tampered cached file"));
            try {await GitHubUpdates.VerifyAsync(release,locator,CancellationToken.None);throw new Exception("Corrupt cache accepted");}
            catch(InvalidDataException) {count++;}
            requests.Clear();
            await GitHubUpdates.DownloadAsync(client,release,locator,new Progress<int>(),CancellationToken.None);
            Check(File.ReadAllBytes(file).SequenceEqual(payload) && requests.Count==1,
                "Velopack downloads a replacement for a corrupt cached package instead of getting stuck on retry");
        }
        using(var cancel=new CancellationTokenSource())
        using(var client=new HttpClient(new ReplyHandler(async (_,token)=>{cancel.Cancel();await Task.Delay(Timeout.Infinite,token);return new(HttpStatusCode.OK);})))
        {
            try {await GitHubUpdates.CheckAsync(client,Locator(),cancel.Token);throw new Exception("Cancellation ignored");}
            catch(OperationCanceledException) {count++;}
        }
        foreach(var channel in new[]{GitHubUpdates.SelfContainedChannel,GitHubUpdates.FrameworkDependentChannel})
        {
            requests.Clear();
            using var client=Client(Feed(channel:channel));
            var locator=Locator(channel:channel);
            var result=await GitHubUpdates.CheckAsync(client,locator,CancellationToken.None);
            var release=result.Release!;
            Check(result.Status==UpdateStatus.Available && release.Channel==channel && requests.Count==2 &&
                requests[1].AbsolutePath.EndsWith($"/releases.{channel}.json"),
                "The installed Velopack channel alone selects its update feed without a runtime probe or preference");
            requests.Clear();
            await GitHubUpdates.DownloadAsync(client,release,locator,new Progress<int>(),CancellationToken.None);
            var packagePath=Path.Combine(locator.PackagesDir!,release.Package.TargetFullRelease.FileName);
            Check(requests.Count==1 && requests[0].AbsolutePath.EndsWith("/download/v0.0.3/"+release.Package.TargetFullRelease.FileName) &&
                File.ReadAllBytes(packagePath).SequenceEqual(payload),"Both channels download only their own package, pinned to the checked tag");
            requests.Clear();
            await GitHubUpdates.DownloadAsync(client,release,locator,new Progress<int>(),CancellationToken.None);
            Check(requests.Count==0,"A verified cached update is reused within its original channel");
            var other=channel==GitHubUpdates.SelfContainedChannel ? GitHubUpdates.FrameworkDependentChannel : GitHubUpdates.SelfContainedChannel;
            try {await GitHubUpdates.DownloadAsync(client,release with {Channel=other},locator,new Progress<int>(),CancellationToken.None);throw new Exception("Cross-channel download accepted");}
            catch(InvalidDataException) {count++;}
            try {await GitHubUpdates.VerifyAsync(release,Locator(channel:other),CancellationToken.None);throw new Exception("Cross-channel apply accepted");}
            catch(InvalidDataException) {count++;}
            Check(requests.Count==0 && File.ReadAllBytes(packagePath).SequenceEqual(payload),"A mismatched channel cannot download or remove another channel's cached package");
            using(var wrongFeed=Client(Feed(channel:other)))
            {
                try {await GitHubUpdates.CheckAsync(wrongFeed,locator,CancellationToken.None);throw new Exception("Cross-channel feed accepted");}
                catch(InvalidDataException) {count++;}
            }
            requests.Clear();
            using(var missingFeed=Client("",HttpStatusCode.NotFound))
                Check((await GitHubUpdates.CheckAsync(missingFeed,locator,CancellationToken.None)).Status==UpdateStatus.NoRelease && requests.Count==2 &&
                    requests[1].AbsolutePath.EndsWith($"/releases.{channel}.json"),"A missing channel never falls back to the other runtime variant");
            using(var sameVersion=Client(Feed("0.0.2",channel:channel)))
                Check((await GitHubUpdates.CheckAsync(sameVersion,locator,CancellationToken.None)).Status==UpdateStatus.UpToDate,
                    "Channel selection does not enable lateral installs or version downgrades");
        }
        requests.Clear();
        using(var client=Client(Feed()))
            Check((await GitHubUpdates.CheckAsync(client,Locator(channel:null),CancellationToken.None)).Release?.Channel==GitHubUpdates.SelfContainedChannel,
                "Legacy manifests without a channel retain Velopack's Windows default");
        requests.Clear();
        using(var client=Client(Feed()))
        {
            try {await GitHubUpdates.CheckAsync(client,Locator(channel:"unknown"),CancellationToken.None);throw new Exception("Unknown channel accepted");}
            catch(InvalidDataException) {Check(requests.Count==0,"Unsupported installation channels fail before requesting an update feed");}
        }
        var portable=Path.Combine(root,"portable");var currentDir=Path.Combine(portable,"current");
        Check(DataPaths.Resolve(currentDir,updateRoot:portable)==Path.Combine(portable,"Data"),"Packaged apps retain Data outside the replaced current directory");
        Check(DataPaths.Resolve(currentDir,Path.Combine(root,"explicit"),portable)==Path.Combine(root,"explicit"),"Restart and capture data overrides survive packaged layout");
        using(var handler=new ConnectionSettings {UseProxy=true,ProxyAddress="http://127.0.0.1:12345"}.CreateHandler())
            Check(handler.UseProxy && handler.Proxy!.GetProxy(new Uri(GitHubUpdates.RepositoryUrl))!.Port==12345 && !handler.UseCookies,"Update transport uses the selected proxy without game cookies");
        using(var handler=new ConnectionSettings {UseProxy=false}.CreateHandler()) Check(!handler.UseProxy,"Disabling the proxy uses direct requests");
        return count;
    }
    private sealed class ReplyHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> reply):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>reply(request,cancellationToken);
    }
}
