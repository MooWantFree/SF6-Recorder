using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Sf6Prototype;

// Public Release redirects and assets avoid the unauthenticated REST API.
// Package URLs remain pinned even if Latest changes during a download.
public sealed class GitHubReleaseSource(HttpClient client, CancellationToken checkToken, string? tag = null) : IUpdateSource
{
    public string? Tag { get; private set; } = tag;
    public Uri? ReleasePage => Tag == null ? null : new($"{GitHubUpdates.RepositoryUrl}/releases/tag/{Uri.EscapeDataString(Tag)}");
    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        if (channel != "win" || appId != GitHubUpdates.PackageId) throw new InvalidDataException("Unexpected update channel or application.");
        if (Tag == null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, GitHubUpdates.RepositoryUrl + "/releases/latest");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, checkToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                using var fallback = await client.GetAsync(request.RequestUri, HttpCompletionOption.ResponseHeadersRead, checkToken).ConfigureAwait(false);
                Tag = ReadTag(fallback);
            }
            else Tag = ReadTag(response);
        }
        using var feedResponse = await client.GetAsync(AssetUrl("releases.win.json"), HttpCompletionOption.ResponseHeadersRead, checkToken).ConfigureAwait(false);
        feedResponse.EnsureSuccessStatusCode();
        await feedResponse.Content.LoadIntoBufferAsync(2 * 1024 * 1024, checkToken).ConfigureAwait(false);
        VelopackAssetFeed feed;
        try {feed = VelopackAssetFeed.FromJson(await feedResponse.Content.ReadAsStringAsync(checkToken).ConfigureAwait(false));}
        catch(Exception ex) when(ex is ArgumentException or FormatException) {throw new InvalidDataException("Invalid release feed.",ex);}
        var packages = (feed.Assets ?? []).Where(a => a?.Type == VelopackAssetType.Full).ToArray();
        if (packages.Length == 0) throw new InvalidDataException("No full update package in the release feed.");
        foreach (var asset in packages) ValidateAsset(asset);
        return new() { Assets = packages };
    }
    private static string ReadTag(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var page = response.RequestMessage?.RequestUri;
        var prefix = GitHubUpdates.RepositoryUrl + "/releases/tag/";
        if (page == null || !page.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Latest did not resolve to this project's release.");
        var tag = Uri.UnescapeDataString(page.AbsoluteUri[prefix.Length..]);
        if (!Regex.IsMatch(tag, @"\A[vV]?[0-9]+\.[0-9]+\.[0-9]+\z")) throw new InvalidDataException("Expected a stable version tag.");
        return tag;
    }
    private Uri AssetUrl(string name)
    {
        if (Tag == null) throw new InvalidOperationException("No release selected.");
        return new($"{GitHubUpdates.RepositoryUrl}/releases/download/{Uri.EscapeDataString(Tag)}/{Uri.EscapeDataString(name)}");
    }
    public static void ValidateAsset(VelopackAsset asset)
    {
        if (asset.PackageId != GitHubUpdates.PackageId || asset.Version == null || asset.Version.IsPrerelease ||
            asset.Type != VelopackAssetType.Full || asset.Size <= 0 ||
            string.IsNullOrEmpty(asset.FileName) || asset.FileName != Path.GetFileName(asset.FileName) ||
            asset.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !asset.FileName.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(asset.SHA256 ?? "", @"\A[0-9a-fA-F]{64}\z"))
            throw new InvalidDataException("Invalid update package metadata.");
    }
    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken = default)
    {
        ValidateAsset(releaseEntry);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        using var response = await client.GetAsync(AssetUrl(releaseEntry.FileName), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var output = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
        var buffer = new byte[65536]; long received = 0; var previous = -1;
        while (true)
        {
            var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            received += read;
            if (received > releaseEntry.Size) throw new InvalidDataException("Update exceeds its declared size.");
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            var percent = (int)(received * 100 / releaseEntry.Size);
            if (percent != previous) { previous = percent; progress(percent); }
        }
        if (received != releaseEntry.Size) throw new InvalidDataException("Incomplete update download.");
    }
}
