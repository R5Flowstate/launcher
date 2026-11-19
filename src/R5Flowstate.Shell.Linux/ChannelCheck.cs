using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Linux.Core;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Read-only probe of the update channel: the same loader the Game Files card
/// uses (ChannelSource.LoadAsync), the same planner behind its size line, and the
/// same assessor behind its health line.
///
/// It fetches the channel manifest and nothing else -- no content manifest, no
/// CAS object, no game file. That makes it the safe way to answer "is the server
/// reachable, and what is it publishing?" without committing to a download.
///
///     R5Flowstate --channel-check
///     R5Flowstate --channel-check https://example/channel/CHANNEL_MANIFEST.json
/// </summary>
static class ChannelCheck
{
    public static async Task<int> RunAsync(string? url, string? installPath, bool deep = false)
    {
        Loc.Initialize(null);

        var settings = LinuxSettings.Load();
        url = string.IsNullOrWhiteSpace(url)
            ? (string.IsNullOrWhiteSpace(settings.ChannelUrl)
                ? LinuxSettings.DefaultChannelUrl
                : settings.ChannelUrl.Trim())
            : url.Trim();
        installPath = string.IsNullOrWhiteSpace(installPath)
            ? (string.IsNullOrWhiteSpace(settings.InstallPath)
                ? LinuxSettings.DefaultInstallPath()
                : settings.InstallPath)
            : installPath.Trim();

        Console.WriteLine($"Channel:  {url}");
        Console.WriteLine($"Install:  {installPath}");
        Console.WriteLine($"Cache:    {ChannelCacheDir}");
        Console.WriteLine();

        using var fetcher = new FileSystemFetcher();
        ChannelManifest? channel;
        try
        {
            channel = await ChannelSource.LoadAsync(
                url, fetcher, ChannelCacheDir, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  channel fetch: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        if (channel is null)
        {
            Console.WriteLine("FAIL  channel fetch returned no manifest.");
            return 1;
        }

        Console.WriteLine($"OK    channel '{channel.Channel}' · gate {channel.EffectiveGateName}" +
                          (channel.Prerelease ? " · prerelease" : ""));

        var tips = new List<(string Name, ChannelTrackTip Tip)>();
        if (channel.Client is not null) tips.Add(("client", channel.Client));
        if (channel.Server is not null) tips.Add(("server", channel.Server));
        if (channel.Platform is not null) tips.Add(("platform", channel.Platform));
        if (channel.Hd is not null) tips.Add(("hd", channel.Hd));

        if (tips.Count == 0)
            Console.WriteLine("WARN  the channel publishes no tracks.");

        foreach (var (name, tip) in tips)
        {
            var bytes = tip.TotalBytes ?? 0;
            Console.WriteLine();
            Console.WriteLine($"  {name}");
            Console.WriteLine($"    version        {tip.CatalogVersion}");
            Console.WriteLine($"    content_hash   {Short(tip.ContentHash)}");
            Console.WriteLine($"    total bytes    {bytes:N0} ({MainViewModel.DownloadSize(bytes)})");
            Console.WriteLine($"    delivery       {(tip.UsesContentManifest ? "content manifest (CAS)" : "archives")}");
            if (!string.IsNullOrWhiteSpace(tip.ContentManifestUrl))
                Console.WriteLine($"    manifest       {tip.ContentManifestUrl}  sha {Short(tip.ContentManifestSha256)}");
            if (!string.IsNullOrWhiteSpace(tip.CasBaseUrl))
                Console.WriteLine($"    cas base       {tip.CasBaseUrl}");
            if (tip.HasPatchChain)
                Console.WriteLine($"    patch chain    {tip.Patches!.Count} patch(es)");
        }

        Console.WriteLine();
        await NotesPassAsync(channel, url, fetcher).ConfigureAwait(false);

        Console.WriteLine();
        if (deep && channel.Client is not null)
        {
            var code = await ManifestPassAsync(channel, fetcher).ConfigureAwait(false);
            if (code != 0)
                return code;
        }

        Console.WriteLine();
        if (!Directory.Exists(installPath))
        {
            Console.WriteLine($"      install folder does not exist yet ({installPath})");
            Console.WriteLine("      nothing is installed here, so the line below describes a first install.");
        }

        var (line, danger) = MainViewModel.InstallDiskLine(channel, installPath);
        Console.WriteLine($"  size line   {(danger ? "SHORTAGE" : "ok")}: {line}");

        var health = ContentInstallService.Assess(
            channel, installPath, requireClient: true, requireServer: false);
        Console.WriteLine($"  health      {health.Overall} · {MainViewModel.HealthLine(health)}");
        Console.WriteLine($"              {health.Summary}");

        Console.WriteLine();
        Console.WriteLine(deep
            ? "Nothing was downloaded beyond the channel and content manifests."
            : "Nothing was downloaded beyond the channel manifest.");
        return danger ? 2 : 0;
    }

    /// <summary>
    /// The Patch Notes tab's own loader, run against the live server: the notes
    /// sit next to the channel manifest, so this is exactly what the tab will
    /// show. Launcher notes come from the launcher feed with the bundled copy
    /// merged in, which is the page the version chip opens.
    /// </summary>
    static async Task NotesPassAsync(ChannelManifest channel, string channelUrl, FileSystemFetcher fetcher)
    {
        Console.WriteLine("  notes");
        List<NotesEntry> game = new();
        try
        {
            game = await NotesSource.LoadAsync(channel, channelUrl, fetcher, ChannelCacheDir)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    FAIL  game notes: {ex.GetType().Name}: {ex.Message}");
        }

        var cached = Path.Combine(ChannelCacheDir, "r5f-notes", "game-NOTES.json");
        Console.WriteLine($"    game          {game.Count} entry(ies)" +
                          (File.Exists(cached) ? "  (cached for offline use)" : "  (bundled copy)"));
        if (game.Count > 0)
            Console.WriteLine($"    newest        {game[0].Date} · {game[0].Title} " +
                              $"({game[0].Items.Count} line(s))");

        List<NotesEntry> launcher = new();
        try
        {
            launcher = await NotesSource.LoadLauncherAsync(fetcher, ChannelCacheDir)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    FAIL  launcher notes: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine($"    launcher      {launcher.Count} entry(ies)");
        if (launcher.Count > 0)
            Console.WriteLine($"    newest        {launcher[0].Date} · {launcher[0].Title}");
    }

    /// <summary>
    /// Resolves the client tip the way the installer does and fetches its content
    /// manifest -- still no CAS object, so no game bytes. Proves the whole
    /// pre-flight: URL resolution against base_url, the digest the channel
    /// carries, the manifest's own validation, and what the file list contains.
    /// </summary>
    static async Task<int> ManifestPassAsync(ChannelManifest channel, FileSystemFetcher fetcher)
    {
        var client = channel.Client!;
        const string preset = "client";
        Console.WriteLine($"  {preset} delivery plan");

        UpdatePlan plan;
        try
        {
            plan = UpdatePlanner.Resolve(client, null, null, channel.BaseUrl);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    FAIL  resolve: {ex.Message}");
            return 1;
        }

        var step = plan.Steps.FirstOrDefault(s => s.Kind == UpdateStepKind.SyncFiles);
        if (step is null)
        {
            Console.WriteLine($"    note  no CAS step for this tip ({plan.Steps.Count} step(s))");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine($"    step          {step.StepId}");
        Console.WriteLine($"    manifest url  {step.ContentManifestUrl}");

        var tmp = Path.Combine(Path.GetTempPath(), "r5f-channel-check-" + Environment.ProcessId);
        Directory.CreateDirectory(tmp);
        try
        {
            var dest = Path.Combine(tmp, "CONTENT_MANIFEST.json");
            await fetcher.DownloadAsync(
                step.ContentManifestUrl!,
                dest,
                step.ContentManifestSha256,
                progress: null,
                CancellationToken.None).ConfigureAwait(false);

            var size = new FileInfo(dest).Length;
            Console.WriteLine($"    fetched       {MainViewModel.DownloadSize(size)} " +
                              $"(sha256 matches the channel: {Short(step.ContentManifestSha256)})");

            ContentManifest man;
            try
            {
                man = ContentManifestIO.Load(dest);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    FAIL  manifest: {ex.Message}");
                return 1;
            }

            var owned = man.Files.Count(f => OverlayPaths.IsOverlayOwned(f.Path));
            var optional = man.Files.Count(f => OverlayPaths.IsOverlayOptional(f.Path));
            var fat = man.Files.Count - owned - optional;

            Console.WriteLine($"    files         {man.Files.Count:N0} " +
                              $"(verified {fat:N0} · overlay-owned {owned:N0} · overlay-optional {optional:N0})");
            Console.WriteLine($"    payload       {man.PayloadBytes:N0} ({MainViewModel.DownloadSize(man.PayloadBytes)})");
            Console.WriteLine($"    objects       {man.ObjectCount:N0}");
            Console.WriteLine($"    content_hash  {Short(man.ContentHash)}" +
                              (HashesMatch(man.ContentHash, client.ContentHash)
                                  ? "  (matches the tip)"
                                  : "  MISMATCH vs the tip"));

            var triad = new[] { "r5apex.exe", "client.dll", "loader.dll" }
                .Select(n => man.Files.Any(f => EndsWith(f.Path, n)))
                .ToArray();
            Console.WriteLine($"    triad         r5apex.exe={(triad[0] ? "yes" : "NO")} " +
                              $"client.dll={(triad[1] ? "yes" : "NO")} loader.dll={(triad[2] ? "yes" : "NO")}");

            if (man.Files.Count > 0)
            {
                Console.WriteLine($"    sample        {man.Files[0].Path} " +
                                  $"({MainViewModel.DownloadSize(man.Files[0].Size)})");
                var largest = man.Files.MaxBy(f => f.Size);
                if (largest is not null)
                    Console.WriteLine($"    largest       {largest.Path} " +
                                      $"({MainViewModel.DownloadSize(largest.Size)})");
            }

            if (man.ObjectCount > 0 && man.Files.Count > 0)
            {
                var key = man.ObjectKey(man.Files[0].Sha256);
                Console.WriteLine($"    cas key       example {key}");
                var baseUrl = (step.CasBaseUrl ?? channel.BaseUrl ?? "").TrimEnd('/');
                Console.WriteLine($"    cas url       example {baseUrl}/{key}");
            }

            Console.WriteLine();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    FAIL  {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    static bool EndsWith(string path, string name) =>
        path.Equals(name, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase);

    static bool HashesMatch(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static string ChannelCacheDir => Path.Combine(LinuxSettings.ConfigDir, "channel");

    static string Short(string? hash) =>
        string.IsNullOrWhiteSpace(hash)
            ? "(none)"
            : hash.Length <= 16 ? hash : hash[..16] + "…";
}
