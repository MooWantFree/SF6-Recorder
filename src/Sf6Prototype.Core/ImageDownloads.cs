using System.IO;
using System.Net;
using System.Net.Http;

namespace Sf6Prototype;

public record ImageDownload(string UrlPath, string Destination, bool Refresh = false);
public interface IImageSource
{
    Task DownloadAsync(ImageDownload image, CancellationToken token);
}
// Owned by the UI dispatcher. Start each newly discovered resource immediately,
// just like independent image elements; only identical in-flight destinations coalesce.
public sealed class ImageDownloads(IImageSource source,Action<ImageDownload> imageSaved,Action<Exception> failed) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop=new();
    private readonly Dictionary<string,Task> _active=new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate=new();
    private Task? _disposal;
    public bool IsCancellationRequested=>_stop.IsCancellationRequested;
    public Task PendingWork {get {lock(_gate) return Task.WhenAll(_active.Values);}}
    public void Start(IEnumerable<ImageDownload> images)
    {
        foreach(var image in images.DistinctBy(i=>Path.GetFullPath(i.Destination),StringComparer.OrdinalIgnoreCase))
        {
            var path=Path.GetFullPath(image.Destination);
            TaskCompletionSource completion;
            lock(_gate)
            {
                if(_stop.IsCancellationRequested) return;
                if(_active.ContainsKey(path) || !image.Refresh && File.Exists(path)) continue;
                completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
                _active.Add(path,completion.Task);
            }
            _=DownloadAsync(image,path,completion);
        }
    }
    private async Task DownloadAsync(ImageDownload image,string path,TaskCompletionSource completion)
    {
        try
        {
            await source.DownloadAsync(image,_stop.Token);
            if(!_stop.IsCancellationRequested) imageSaved(image);
        }
        catch(OperationCanceledException) when(_stop.IsCancellationRequested) { }
        catch(Exception ex) when(ex is not OutOfMemoryException)
        {
            if(!_stop.IsCancellationRequested) failed(ex);
        }
        finally
        {
            lock(_gate) {_active.Remove(path);completion.TrySetResult();}
        }
    }
    public void Cancel()=>_stop.Cancel();
    public ValueTask DisposeAsync()=>new(_disposal ??=StopAsync());
    private async Task StopAsync()
    {
        Cancel();await PendingWork;
        (source as IDisposable)?.Dispose();_stop.Dispose();
    }
}

public sealed class ImageClient : IImageSource, IDisposable
{
    private readonly HttpClient _http;
    public ImageClient(ConnectionSettings settings, SavedSession session):this(settings.CreateHandler(CookieVault.ToContainer(session)),session.UserAgent) { }
    internal ImageClient(HttpMessageHandler handler,string userAgent)
    {
        // Images can use the current session but never write credentials back. A late
        // image response must not overwrite a refreshed session or restore a logged-out account.
        _http = new HttpClient(handler)
        { Timeout = TimeSpan.FromSeconds(25), DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Referrer = new Uri(BucklerClient.Origin + "/6/buckler/");
    }
    public async Task DownloadAsync(ImageDownload image, CancellationToken token)
    {
        if (!image.UrlPath.StartsWith("/6/buckler/assets/images/material/", StringComparison.Ordinal) || image.UrlPath.Contains(".."))
            throw new ArgumentException(Strings.InvalidImageUrl);
        token.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Get, BucklerClient.Origin + image.UrlPath);
        if (image.Refresh) request.Headers.CacheControl = new() { NoCache = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) != true)
            throw new InvalidDataException(Strings.InvalidImageFormat);
        if (response.Content.Headers.ContentLength > 5_000_000) throw new InvalidDataException(Strings.ImageTooLarge);
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        var buffer = new byte[81920]; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (bytes.Length + read > 5_000_000) throw new InvalidDataException(Strings.ImageTooLarge);
            bytes.Write(buffer, 0, read);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(image.Destination)!);
        var temporary=image.Destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes.ToArray(), token);
            token.ThrowIfCancellationRequested();
            if(File.Exists(image.Destination)) File.Replace(temporary,image.Destination,null);
            else File.Move(temporary,image.Destination);
        }
        finally { File.Delete(temporary); }
    }
    public void Dispose() => _http.Dispose();
}
