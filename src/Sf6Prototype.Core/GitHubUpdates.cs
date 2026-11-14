using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using Velopack;
using Velopack.Locators;

namespace Sf6Prototype;

public enum UpdateStatus { UpToDate, Available, NoRelease, NotInstalled }
public sealed record UpdateRelease(Version Version, Uri Page, string Tag, UpdateInfo Package,
    string Channel = GitHubUpdates.SelfContainedChannel);
public sealed record UpdateCheckResult(UpdateStatus Status, UpdateRelease? Release = null);

public static class GitHubUpdates
{
    public const string PackageId = "SF6Recorder";
    // Keep the existing channel for self-contained installations, including older releases.
    public const string SelfContainedChannel = "win";
    public const string FrameworkDependentChannel = "win-framework-dependent";
    public const string RepositoryUrl = "https://github.com/MooWantFree/SF6-Recorder";
    public static Version CurrentVersion { get; } = Version.Parse((Assembly.GetEntryAssembly() ?? typeof(GitHubUpdates).Assembly)
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]);
    public static string VersionLabel => $"v{CurrentVersion.ToString(3)}";

    public static HttpClient CreateClient(ConnectionSettings settings)
    {
        var handler = settings.CreateHandler();
        handler.AllowAutoRedirect = true; handler.MaxAutomaticRedirections = 5;
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SF6-Recorder/{CurrentVersion.ToString(3)}");
        return client;
    }
    public static async Task<UpdateCheckResult> CheckAsync(ConnectionSettings settings, CancellationToken token)
    {
        using var client = CreateClient(settings);
        return await CheckAsync(client, VelopackLocator.Current, token).ConfigureAwait(false);
    }
    public static async Task<UpdateCheckResult> CheckAsync(HttpClient client, IVelopackLocator locator, CancellationToken token)
    {
        if (locator.CurrentlyInstalledVersion == null) return new(UpdateStatus.NotInstalled);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var source = new GitHubReleaseSource(client, timeout.Token, channel: locator.Channel ?? SelfContainedChannel);
        var manager = new UpdateManager(source, locator: locator);
        try
        {
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (update == null) return new(UpdateStatus.UpToDate);
            return new(UpdateStatus.Available, new(Version.Parse(update.TargetFullRelease.Version.ToString().Split('+')[0]),
                source.ReleasePage!, source.Tag!, update, source.Channel));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return new(UpdateStatus.NoRelease); }
    }
    public static async Task DownloadAsync(UpdateRelease release, ConnectionSettings settings, IProgress<int> progress, CancellationToken token)
    {
        using var client = CreateClient(settings);
        await DownloadAsync(client,release,VelopackLocator.Current,progress,token).ConfigureAwait(false);
    }
    public static async Task DownloadAsync(HttpClient client, UpdateRelease release, IVelopackLocator locator, IProgress<int> progress, CancellationToken token)
    {
        ValidateRelease(release, locator);
        var cached=Path.Combine(locator.PackagesDir!,release.Package.TargetFullRelease.FileName);
        if(File.Exists(cached))
        {
            try {await VerifyAsync(release,locator,token).ConfigureAwait(false);progress.Report(100);return;}
            catch(InvalidDataException) {File.Delete(cached);}
        }
        var source = new GitHubReleaseSource(client, token, release.Tag, release.Channel);
        var manager = new UpdateManager(source, locator: locator);
        await manager.DownloadUpdatesAsync(release.Package, progress.Report, token).ConfigureAwait(false);
        // Cached packages must be hashed too before starting an updater.
        await VerifyAsync(release, locator, token).ConfigureAwait(false);
    }
    public static async Task VerifyAsync(UpdateRelease release, IVelopackLocator locator, CancellationToken token)
    {
        var asset = release.Package.TargetFullRelease;
        ValidateRelease(release, locator);
        var path = Path.Combine(locator.PackagesDir!, asset.FileName);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
        if (file.Length != asset.Size || !hash.Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded update checksum does not match.");
    }
    private static void ValidateRelease(UpdateRelease release, IVelopackLocator locator)
    {
        if (release.Channel != (locator.Channel ?? SelfContainedChannel))
            throw new InvalidDataException("The update belongs to a different installation channel.");
        GitHubReleaseSource.ValidateAsset(release.Package.TargetFullRelease, release.Channel);
    }
    public static async Task<Action> PrepareRestartAsync(UpdateRelease release, string dataDirectory)
    {
        var locator = VelopackLocator.Current;
        await VerifyAsync(release, locator, CancellationToken.None);
        var dataPath = Path.GetFullPath(dataDirectory);
        var content = Path.TrimEndingDirectorySeparator(Path.GetFullPath(locator.AppContentDir!));
        if (dataPath.Equals(content, StringComparison.OrdinalIgnoreCase) ||
            dataPath.StartsWith(content + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Application data must be outside the versioned application directory.");
        // Schedule after draining downloads and writes; do not abruptly exit.
        var manager = new UpdateManager(new Velopack.Sources.SimpleFileSource(new DirectoryInfo(locator.PackagesDir!)));
        return () => manager.WaitExitThenApplyUpdates(release.Package.TargetFullRelease, silent: true, restart: true,
            restartArgs: ["--data-dir", dataPath]);
    }
}
