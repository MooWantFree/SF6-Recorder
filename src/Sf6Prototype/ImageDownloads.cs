using System.IO;
using System.Net;
using System.Net.Http;

namespace Sf6Prototype;

public record ImageDownload(string UrlPath, string Destination, bool Refresh = false);
public interface IImageSource
{
    Task DownloadAsync(ImageDownload image, CancellationToken token);
}
public static class ImageDownloads
{
    public static async Task RunAsync(IEnumerable<ImageDownload> images, IImageSource source, Func<ImageDownload,Task> imageSaved, CancellationToken token)
    {
        foreach (var image in images.DistinctBy(i => i.Destination))
        {
            token.ThrowIfCancellationRequested();
            if (!image.Refresh && File.Exists(image.Destination)) continue;
            await source.DownloadAsync(image, token);
            token.ThrowIfCancellationRequested();
            await imageSaved(image);
        }
    }
}

public sealed class ImageClient : IImageSource, IDisposable
{
    private readonly HttpClient _http;
    public ImageClient(ConnectionSettings settings, SavedSession session)
    {
        // Images can use the current session but never write credentials back. A late
        // image response must not overwrite a refreshed session or restore a logged-out account.
        _http = new HttpClient(settings.CreateHandler(CookieVault.ToContainer(session)))
        { Timeout = TimeSpan.FromSeconds(25), DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(session.UserAgent);
        _http.DefaultRequestHeaders.Referrer = new Uri(BucklerClient.Origin + "/6/buckler/");
    }
    public async Task DownloadAsync(ImageDownload image, CancellationToken token)
    {
        if (!image.UrlPath.StartsWith("/6/buckler/assets/images/material/", StringComparison.Ordinal) || image.UrlPath.Contains(".."))
            throw new ArgumentException("无效的官网图片地址。");
        await RequestGate.Shared.RunAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BucklerClient.Origin + image.UrlPath);
            if (image.Refresh) request.Headers.CacheControl = new() { NoCache = true };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal) != true)
                throw new InvalidDataException("官网头像格式无效。");
            if (response.Content.Headers.ContentLength > 5_000_000) throw new InvalidDataException("头像文件过大。");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920]; int read;
            while ((read = await input.ReadAsync(buffer, token)) != 0)
            {
                if (bytes.Length + read > 5_000_000) throw new InvalidDataException("头像文件过大。");
                bytes.Write(buffer, 0, read);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(image.Destination)!);
            try
            {
                await File.WriteAllBytesAsync(image.Destination + ".tmp", bytes.ToArray(), token);
                token.ThrowIfCancellationRequested();
                File.Move(image.Destination + ".tmp", image.Destination, true);
            }
            finally { File.Delete(image.Destination + ".tmp"); }
            return true;
        }, token);
    }
    public void Dispose() => _http.Dispose();
}
