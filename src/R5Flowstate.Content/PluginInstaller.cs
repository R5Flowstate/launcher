using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

public sealed class PluginInstallException : Exception
{
    public PluginInstallException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}

public readonly record struct InstalledPluginFile(string Id, string Version, string Tier);

public static class PluginInstaller
{
    public const string WasmFileName = "plugin.wasm";
    public const string NativeFileName = "plugin.dll";
    public const int MaxIds = 64;
    public const int MaxVersions = 8;

    static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    public static string FileName(string tier) =>
        tier == "native" ? NativeFileName : WasmFileName;

    public static string RelativePath(string id, string version, string tier) =>
        "platform/plugins/" + id + "/" + version + "/" + FileName(tier);

    public static bool IsInstalled(string installRoot, CuratedPlugin plugin)
    {
        if (!SafePath.TryJoin(installRoot, RelativePath(plugin.Id, plugin.Version, plugin.Tier), out var path))
            return false;
        try
        {
            if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                return false;
            var info = new FileInfo(path);
            if (info.Length != plugin.ArtifactSize)
                return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hex = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            return string.Equals(hex, plugin.ArtifactSha256, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static IReadOnlyList<InstalledPluginFile> Scan(string installRoot, Action<string>? log = null)
    {
        var found = new List<InstalledPluginFile>();
        if (!SafePath.TryJoin(installRoot, "platform/plugins", out var root) || !Directory.Exists(root))
            return found;
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
            log?.Invoke("plugins directory is a reparse point");
            return found;
        }

        var ids = 0;
        foreach (var idDir in Directory.EnumerateDirectories(root))
        {
            if (File.GetAttributes(idDir).HasFlag(FileAttributes.ReparsePoint))
                continue;
            var id = Path.GetFileName(idDir);
            if (!PluginStatement.IsId(id))
                continue;
            if (++ids > MaxIds)
            {
                log?.Invoke("plugin install tree exceeds " + MaxIds + " ids");
                break;
            }

            var versions = 0;
            foreach (var verDir in Directory.EnumerateDirectories(idDir))
            {
                if (File.GetAttributes(verDir).HasFlag(FileAttributes.ReparsePoint))
                    continue;
                var version = Path.GetFileName(verDir);
                if (!PluginStatement.IsVersion(version))
                    continue;
                if (++versions > MaxVersions)
                {
                    log?.Invoke("plugin " + id + " exceeds " + MaxVersions + " versions");
                    break;
                }

                if (RealFile(Path.Combine(verDir, WasmFileName)))
                    found.Add(new InstalledPluginFile(id, version, "wasm"));
                else if (RealFile(Path.Combine(verDir, NativeFileName)))
                    found.Add(new InstalledPluginFile(id, version, "native"));
            }
        }

        return found;
    }

    public static async Task InstallAsync(
        string installRoot,
        CuratedPlugin plugin,
        HttpMessageHandler? handler,
        CancellationToken cancel,
        Action<string>? log = null)
    {
        // The local server shares this install, so a server-realm native plugin would run on the player's PC too.
        if (plugin.Tier == "native" && !plugin.FirstParty)
            throw new PluginInstallException("refused", "native plugins must be first-party");
        if (!PluginStatement.IsArtifactUrl(plugin.ArtifactUrl, plugin.ArtifactSha256))
            throw new PluginInstallException("bad_url", "artifact url is not on the content host");
        if (plugin.ArtifactUrl.Contains("thunderstore", StringComparison.OrdinalIgnoreCase))
            throw new PluginInstallException("bad_url", "plugins are not installed from Thunderstore");

        if (!SafePath.TryJoin(installRoot, RelativePath(plugin.Id, plugin.Version, plugin.Tier), out var dest))
            throw new PluginInstallException("bad_path", "plugin path is outside the install");
        var dir = Path.GetDirectoryName(dest);
        if (string.IsNullOrEmpty(dir))
            throw new PluginInstallException("bad_path", "plugin directory is missing");
        Directory.CreateDirectory(dir);
        if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
            throw new PluginInstallException("bad_path", "plugin directory is a reparse point");
        if (File.Exists(dest) && File.GetAttributes(dest).HasFlag(FileAttributes.ReparsePoint))
            throw new PluginInstallException("bad_path", "plugin file is a reparse point");

        var tmp = dest + ".tmp";
        var owns = handler is null;
        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        if (handler is HttpClientHandler httpHandler)
            httpHandler.AllowAutoRedirect = false;

        try
        {
            using var http = new HttpClient(handler, disposeHandler: owns) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "R5Flowstate-Plugins/1");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(FetchTimeout);
            using var response = await http.GetAsync(plugin.ArtifactUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
                throw new PluginInstallException("bad_url", "artifact redirect was refused");
            if (status != 200)
                throw new PluginInstallException("http", "artifact returned HTTP " + status);

            var declared = response.Content.Headers.ContentLength;
            if (declared is > 0 && declared.Value != plugin.ArtifactSize)
                throw new PluginInstallException("hash_mismatch", "artifact size does not match the statement");

            // Stop at the signed size so a hostile host cannot grow the buffer.
            await using var input = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
            {
                done += read;
                if (done > plugin.ArtifactSize)
                    throw new PluginInstallException("hash_mismatch", "artifact is larger than the statement");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
            }

            await output.FlushAsync(cts.Token).ConfigureAwait(false);
            output.Close();
            if (done != plugin.ArtifactSize)
                throw new PluginInstallException("hash_mismatch", "artifact size does not match the statement");
            var hex = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(hex, plugin.ArtifactSha256, StringComparison.Ordinal))
                throw new PluginInstallException("hash_mismatch", "artifact sha256 does not match the statement");

            File.Move(tmp, dest, overwrite: true);
            log?.Invoke("installed " + plugin.Id + "@" + plugin.Version);
        }
        catch (PluginInstallException)
        {
            TryDelete(tmp);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            if (ex is TaskCanceledException && cancel.IsCancellationRequested)
                throw;
            throw new PluginInstallException(ex is HttpRequestException or TaskCanceledException ? "network" : "io", ex.Message, ex);
        }
    }

    public static string? Uninstall(string installRoot, string id, Action<string>? log = null)
    {
        if (!PluginStatement.IsId(id))
            return "bad id";
        PluginEnablement.RemoveId(installRoot, id);
        if (!SafePath.TryJoin(installRoot, "platform/plugins/" + id, out var dir))
            return "bad path";
        if (!Directory.Exists(dir))
            return null;
        if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
            return "plugin directory is a reparse point";
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = 0,
                 }))
        {
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                return "plugin tree contains a reparse point";
        }

        Directory.Delete(dir, recursive: true);
        log?.Invoke("uninstalled " + id);
        return null;
    }

    static bool RealFile(string path)
    {
        try
        {
            return File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
