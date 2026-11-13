using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Sf6Prototype;

internal static class ImageDownloadChecks
{
    public static async Task<int> Run()
    {
        var count=0;void Check(bool value,string message){if(!value)throw new Exception(message);count++;}
        var root=Path.Combine(Environment.CurrentDirectory,".local","checks",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        ImageDownload[] Images(string prefix,int total)=>Enumerable.Range(0,total)
            .Select(i=>new ImageDownload(prefix+i,Path.Combine(root,$"{prefix}-{i}.png"))).ToArray();
        var images=Images("parallel",9);File.WriteAllText(images[8].Destination,"cached");
        var source=new ControlledImages();var saved=new ConcurrentBag<string>();var errors=new ConcurrentBag<Exception>();
        var firstSaved=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var downloads=new ImageDownloads(source,image=>{saved.Add(image.UrlPath);firstSaved.TrySetResult();},errors.Add);
        downloads.Start(images.Concat([images[0] with {Destination=Path.Combine(root,"sub","..","parallel-0.png")}]));
        Check(source.Active==8 && source.Calls==8 && saved.IsEmpty,"All uncached portraits start immediately without waiting for a four-image batch");
        downloads.Start(images);
        Check(source.Calls==8,"Repeated starts coalesce destinations that are already downloading");
        var later=Images("later",1);downloads.Start(later);
        Check(source.Calls==9 && source.Active==9,"A newly discovered avatar starts while all earlier portraits remain blocked");
        source.Release(images[2].UrlPath);await firstSaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(saved.Single()==images[2].UrlPath && File.Exists(images[2].Destination),"A completed portrait is displayed before slower images finish");
        source.ReleaseAll();await downloads.PendingWork;
        Check(source.Active==0 && saved.Count==9 && errors.IsEmpty,"Independent downloads finish and notify once each");
        Check(File.ReadAllText(images[8].Destination)=="cached","Cached portraits remain unchanged");
        downloads.Start(images);await downloads.PendingWork;
        Check(source.Calls==9,"Already cached images are not requested on the next page load");

        var cancelledSource=new ControlledImages();var notified=0;
        await using(var cancelled=new ImageDownloads(cancelledSource,_=>Interlocked.Increment(ref notified),errors.Add))
        {
            var pending=Images("cancel",8);cancelled.Start(pending);cancelled.Cancel();await cancelled.PendingWork;
            cancelled.Start(Images("after-cancel",1));
            Check(cancelledSource.Calls==8 && cancelledSource.Active==0 && notified==0 && pending.All(i=>!File.Exists(i.Destination)),
                "Cancellation drains every active writer and prevents further requests or callbacks");
        }
        Check(cancelledSource.Disposed,"The HTTP source is disposed only after its active requests stop");
        var failures=new ControlledImages();var failedImages=Images("failed",8);var succeeded=new ConcurrentBag<string>();
        await using(var failed=new ImageDownloads(failures,i=>succeeded.Add(i.UrlPath),errors.Add))
        {
            failed.Start(failedImages);failures.Fail(failedImages[0].UrlPath);failures.ReleaseAll();await failed.PendingWork;
            Check(failures.Active==0 && succeeded.Count==7 && errors.Count==1,"One broken image leaves all other downloads running");
            failed.Start([failedImages[0]]);await failed.PendingWork;
            Check(failures.Calls==9 && File.Exists(failedImages[0].Destination),"An uncached failed portrait can be retried on a later load");
        }

        // A held real data gate must not delay either kind of image HTTP request.
        var apiEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApi=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api=RequestGate.Shared.RunAsync(()=>{apiEntered.SetResult();return releaseApi.Task;},CancellationToken.None);
        await apiEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var handler=new HeldImagesHandler();using var client=new ImageClient(handler,"Sf6Checks/1.0");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var httpImages=Images("http",8).Select((i,n)=>i with {UrlPath=n==0
            ? "/6/buckler/assets/images/material/icon/avatar.jpg"
            : "/6/buckler/assets/images/material/character/character_"+i.UrlPath+"_l.png"}).ToArray();
        var tasks=httpImages.Select(i=>client.DownloadAsync(i,timeout.Token)).ToArray();
        try
        {
            Check(handler.Calls==8 && handler.Active==8 && !api.IsCompleted,"Avatar and character HTTP requests all start despite the occupied data gate");
            handler.Release.TrySetResult();await Task.WhenAll(tasks);
            Check(httpImages.All(i=>File.Exists(i.Destination)) && !Directory.EnumerateFiles(root,"*.tmp").Any(),"Concurrent HTTP saves leave complete files and no temporary files");
        }
        finally {handler.Release.TrySetResult();releaseApi.TrySetResult(true);await Task.WhenAll(tasks);await api;}

        // The bundled roster makes first-run portraits available before any official metadata.
        var profileRoot=Path.Combine(root,"profile");var profiles=new ProfileCache(profileRoot);var profilesSource=new Profiles();
        var portraits=profiles.CharacterDownloads();
        Check(portraits.Count>20 && profiles.Characters.Count==0,"Fresh installations know portrait URLs before downloading any battle or profile");
        var earlySource=new ControlledImages();var earlySaved=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var early=new ImageDownloads(earlySource,_=>earlySaved.TrySetResult(),errors.Add);
        early.Start(portraits);
        var avatar=await profiles.RefreshAvatarAsync(profilesSource,Profiles.Owner,CancellationToken.None);early.Start(avatar);
        Check(profilesSource.PlayRequests==0 && earlySource.Calls==portraits.Count+1 && profiles.Scores(Profiles.Owner)==null,
            "Avatar discovery and all portrait downloads precede pagination without reading current scores early");
        earlySource.Release(avatar.Single().UrlPath);await earlySaved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(earlySource.Active==portraits.Count && File.Exists(avatar.Single().Destination),"An avatar is saved while all portrait downloads are still pending");
        early.Start(await profiles.RefreshPlayAsync(profilesSource,Profiles.Owner,CancellationToken.None,captureScores:true));
        Check(profilesSource.PlayRequests==1 && profiles.Scores(Profiles.Owner)?.Characters.Single().Lp==20000,
            "The later score update requires only one play request");
        earlySource.ReleaseAll();await early.PendingWork;
        Check(profiles.CharacterDownloads().Count==0 && profiles.CharacterImage(1)!=null && profiles.CharacterImage(16)!=null,
            "Bundled and observed portraits are usable by the archive and cached for later loads");
        return count;
    }
    private sealed class ControlledImages : IImageSource,IDisposable
    {
        private readonly object _gate=new();
        private readonly Dictionary<string,TaskCompletionSource> _releases=[];
        private bool _allReleased;
        public int Calls,Active;
        public bool Disposed;
        public async Task DownloadAsync(ImageDownload image,CancellationToken token)
        {
            TaskCompletionSource release;
            lock(_gate)
            {
                release=new(TaskCreationOptions.RunContinuationsAsynchronously);_releases[image.UrlPath]=release;
                Calls++;Active++;if(_allReleased)release.TrySetResult();
            }
            try
            {
                await release.Task.WaitAsync(token);Directory.CreateDirectory(Path.GetDirectoryName(image.Destination)!);
                await File.WriteAllTextAsync(image.Destination,"image",token);
            }
            finally {lock(_gate)Active--;}
        }
        public void Release(string key) {lock(_gate)_releases[key].TrySetResult();}
        public void Fail(string key) {lock(_gate)_releases[key].TrySetException(new HttpRequestException("Synthetic failure"));}
        public void ReleaseAll() {lock(_gate){_allReleased=true;foreach(var release in _releases.Values)release.TrySetResult();}}
        public void Dispose() {if(Active!=0)throw new Exception("Disposed during a download");Disposed=true;}
    }
    private sealed class HeldImagesHandler : HttpMessageHandler
    {
        public int Calls,Active;
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Interlocked.Increment(ref Calls);Interlocked.Increment(ref Active);
            try
            {
                await Release.Task.WaitAsync(token);
                var content=new ByteArrayContent([1,2,3]);content.Headers.ContentType=new MediaTypeHeaderValue("image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) {Content=content};
            }
            finally {Interlocked.Decrement(ref Active);}
        }
    }
    private sealed class Profiles : IProfileSource
    {
        public const string Owner="3396336472";
        public int PlayRequests;
        public Task<string> FetchCardAsync(string owner,CancellationToken token)=>Task.FromResult("""{"fighter_name":"Player","icon_file_name":"test_icon"}""");
        public Task<string> FetchPlayAsync(string owner,CancellationToken token)
        {
            PlayRequests++;
            return Task.FromResult("""{"pageProps":{"fighter_banner_info":{"personal_info":{"short_id":3396336472,"fighter_id":"Player"}},"play":{"current_season_id":14,"season_ids":[14],"character_league_infos":[{"character_id":16,"character_name":"Juri","character_tool_name":"juri","league_info":{"league_point":20000,"league_rank":30,"master_rating":0,"master_league":0}}]}}}""");
        }
    }
}
