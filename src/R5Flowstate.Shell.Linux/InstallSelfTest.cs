using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Install / update / verify / repair, driven through the real content engine
/// (R5Flowstate.Content) against a synthetic channel served from a folder on disk.
///
/// The engine accepts file:// and absolute paths for both the channel and the CAS
/// objects — the local mode upstream uses for its own tests — so the whole pipeline
/// runs here without a network and without touching anything outside a temp folder.
/// That is the point: what needs proving on Linux is that the port drives the
/// pipeline correctly (paths, digests, atomicity, repair), not that the CDN is up.
///
/// The pack is shaped like the real one: client track installed from a
/// CONTENT_MANIFEST through cas/&lt;shard&gt;/&lt;sha256&gt;, with the triad the health
/// assessor requires (r5apex.exe / client.dll / loader.dll).
/// </summary>
static class InstallSelfTest
{
    public static void Run(Action<string, bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "r5f-install-selftest-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(root);
            // Run off the UI thread: the pipeline is async and the headless
            // dispatcher would otherwise be the only place a continuation could go.
            Task.Run(() => Pipeline(check, root)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            check("install pipeline", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(root);
        }
    }

    static void Pipeline(Action<string, bool, string> report, string root)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var channel = Path.Combine(root, "channel");
        var install = Path.Combine(root, "install");

        // ---- pack v1 ---------------------------------------------------------
        // Shaped like the real client track: the triad the health assessor wants
        // (r5apex.exe / client.dll / loader.dll), a pack file it actually verifies,
        // and a script the platform overlay would own.
        var v1 = new List<FileSpec>
        {
            new("r5apex.exe", PseudoBytes(64_000, 0x11)),
            new("client.dll", PseudoBytes(128_000, 0x22)),
            new("loader.dll", PseudoBytes(4_096, 0x33)),
            new("paks/Win64/r5f_test.rpak", PseudoBytes(96_000, 0x44)),
            new("scripts/vscripts/r5f_test.nut", Encoding.UTF8.GetBytes("print(\"selftest\")\n")),
        };
        var contentHashV1 = WritePack(channel, "1.0.0", v1);

        // ---- 1. fresh install ------------------------------------------------
        var progress = new CaptureProgress();
        var state = Install(channel, install, progress, out var error);
        check("install: completed", error.Length == 0 && !state.Incomplete, error.Length > 0 ? error : "ok");
        check("install: client ready", state.ClientReady);
        check("install: every file landed byte-exact", PackMatches(install, v1, out var mismatch), mismatch);
        check("install: INSTALL_STATE written",
            File.Exists(Path.Combine(install, ProductConstants.InstallStateFileName)));
        check("install: reported progress", progress.Count > 0, $"{progress.Count} reports");
        check("install: progress named the manifest phase",
            progress.Phases.Any(p => p.Contains("manifest", StringComparison.OrdinalIgnoreCase)
                                     || p.Contains("download", StringComparison.OrdinalIgnoreCase)),
            string.Join(",", progress.Phases.Distinct().Take(6)));

        // ---- 2. health ------------------------------------------------------
        var cachedManifest = InstallHealthAssessor.FindCachedContentManifest(install, "client", "1.0.0");
        check("install: content manifest cached where the assessor looks",
            cachedManifest is not null && File.Exists(cachedManifest), cachedManifest ?? "(null)");

        var health = Assess(channel, install);
        check("assess: ready after install", health.IsReady, health.Summary);
        check("assess: health line is the UI's ready wording",
            MainViewModel.HealthLine(health) == Loc.Get("health_ready"), MainViewModel.HealthLine(health));

        // ---- 3. size line + planner numbers ---------------------------------
        var declared = v1.Sum(f => (long)f.Bytes.Length);
        var fresh = Path.Combine(root, "install-size-probe");
        Directory.CreateDirectory(fresh);
        check("planner: an empty target owes the whole pack",
            MainViewModel.TryMeasureInstallDisk(
                LoadChannel(channel), fresh, out var planned, out var need, out _)
            && planned == declared && need >= planned,
            $"planned={planned} declared={declared} need={need}");
        var (freshLine, freshDanger) = MainViewModel.InstallDiskLine(LoadChannel(channel), fresh);
        check("planner: the size line is the disk_about copy",
            !freshDanger && freshLine.Contains("free"), freshLine);
        check("planner: an install already at the tip owes nothing",
            MainViewModel.TryMeasureInstallDisk(
                LoadChannel(channel), install, out var atTip, out _, out _) && atTip == 0,
            $"planned={atTip}");

        // ---- 4. what the engine verifies, and what it repairs ----------------
        // The marker DLLs at the install root are overlay-OPTIONAL: the engine's
        // own list says they are the player's to replace, so neither the cheap
        // assessor nor the deep hash looks at them. That is deliberate (players
        // drop their own client.dll / loader.dll there), and it is why the deep
        // Verify below is asserted on a pack file instead.
        var clientDll = Path.Combine(install, "client.dll");
        var officialClientDll = v1.First(f => f.Path == "client.dll").Bytes;
        File.WriteAllBytes(clientDll, PseudoBytes(128_000, 0x99));
        check("custom build: the cheap check ignores client.dll", Assess(channel, install).IsReady);

        var custom = ContentInstallService.VerifyExistingAsync(
            LoadChannel(channel), install, null, CancellationToken.None, keepLocalFiles: false, deep: true)
            .GetAwaiter().GetResult();
        check("custom build: the deep check ignores it too", custom.IsReady,
            $"{custom.Summary} (broken={custom.BrokenFileCount})");

        // Not verified, but not rebuilt either: a fresh INSTALL_STATE already at
        // the tip makes the planner exclude the whole track, so an install with
        // nothing to do never even scans the tree -- which is what keeps a 42 GiB
        // install from re-hashing itself every time the button is pressed.
        Install(channel, install, null, out var sameTipErr);
        check("custom build: an install at the same tip touches nothing",
            sameTipErr.Length == 0
            && File.ReadAllBytes(clientDll).AsSpan().SequenceEqual(PseudoBytes(128_000, 0x99)),
            sameTipErr.Length > 0 ? sameTipErr : "left alone");

        // 4b. A pack file is engine-owned, so it IS verified. Same-size damage is
        //     invisible to the size-only assessor -- hence the separate Verify.
        var rpak = Path.Combine(install, "paks", "Win64", "r5f_test.rpak");
        File.WriteAllBytes(rpak, PseudoBytes(96_000, 0xEE));
        check("assess: same-size damage is invisible to the cheap check", Assess(channel, install).IsReady);

        var verified = ContentInstallService.VerifyExistingAsync(
            LoadChannel(channel), install, null, CancellationToken.None, keepLocalFiles: false, deep: true)
            .GetAwaiter().GetResult();
        check("verify: the deep hash catches it", verified.NeedsRepair, verified.Summary);
        check("verify: counts the broken file", verified.BrokenFileCount >= 1,
            $"{verified.BrokenFileCount} broken: {string.Join("; ", verified.Reasons.Take(2))}");

        // 4c. A short file is what the cheap check does catch, and what repair fixes.
        File.WriteAllBytes(rpak, new byte[10]);
        var truncated = Assess(channel, install);
        check("assess: truncation is caught", truncated.NeedsRepair, truncated.Summary);
        check("assess: the health line says how many files",
            MainViewModel.HealthLine(truncated) == Loc.Format("health_missing_n", truncated.BrokenFileCount),
            MainViewModel.HealthLine(truncated));

        var repaired = ContentInstallService.EnsureReadyAsync(
            LoadChannel(channel), install, requireClient: true, requireServer: false,
            autoRepairCorrupt: true, autoApplyUpdates: false,
            fetcher: null, progress: null, cancel: CancellationToken.None,
            decideOverlay: null, allowContent: true, allowPlatform: true, keepLocalFiles: false)
            .GetAwaiter().GetResult();
        check("repair: EnsureReady restores the pack file",
            FileMatches(install, v1.First(f => f.Path == "paks/Win64/r5f_test.rpak"), out var rpakState),
            rpakState);
        check("repair: ready afterwards", repaired.IsReady || Assess(channel, install).IsReady, repaired.Summary);

        // A repair is `forceReinstall`, so the track re-resolves and the official
        // write does reach overlay-optional files: the player's own client.dll is
        // replaced (upstream's trade-off; KeepLocalFiles=KeepAll is the opt-out).
        // Worth pinning -- it is the one button that can undo a custom build even
        // though the health check never flagged it.
        check("repair: a forced repair writes official bytes over the player's build",
            File.ReadAllBytes(clientDll).AsSpan().SequenceEqual(officialClientDll),
            "official bytes back");

        // ---- 5. channel moves to v2 -----------------------------------------
        var v2 = new List<FileSpec>
        {
            new("r5apex.exe", PseudoBytes(64_000, 0xAA)),
            new("client.dll", PseudoBytes(128_000, 0x22)),
            new("loader.dll", PseudoBytes(4_096, 0x33)),
            new("paks/Win64/r5f_test.rpak", PseudoBytes(96_000, 0x44)),
            new("scripts/vscripts/r5f_test.nut", Encoding.UTF8.GetBytes("print(\"selftest v2\")\n")),
        };
        var contentHashV2 = WritePack(channel, "1.0.1", v2);
        check("channel: the two tips are different builds", contentHashV1 != contentHashV2);

        var drifted = Assess(channel, install);
        check("assess: reports the new tip as an update", drifted.NeedsUpdate, drifted.Summary);

        var updated = Install(channel, install, null, out var updateError);
        check("update: completed", updateError.Length == 0 && !updated.Incomplete, updateError);
        check("update: new bytes on disk", PackMatches(install, v2, out var stale), stale);
        check("update: assess ready at the new tip", Assess(channel, install).IsReady);

        // ---- 6. refusals ----------------------------------------------------
        // 6a. a channel whose content-manifest digest does not match the bytes.
        var tamper = Path.Combine(root, "tamper");
        var tamperInstall = Path.Combine(root, "install-tamper");
        WritePack(tamper, "1.0.0", v1, tamperManifestSha: true);
        var tamperState = Install(tamper, tamperInstall, null, out var tamperError);
        check("refuse: a tampered CONTENT_MANIFEST is not installed",
            tamperError.Length > 0 || tamperState.Incomplete,
            tamperError.Length > 0 ? tamperError : "state flagged incomplete");
        check("refuse: no game file was written from it",
            !File.Exists(Path.Combine(tamperInstall, "r5apex.exe")),
            string.Join(",", SafeList(tamperInstall)));

        // 6b. a manifest entry that climbs out of the install directory.
        var escape = Path.Combine(root, "escape");
        var escapeInstall = Path.Combine(root, "install-escape");
        WritePack(escape, "1.0.0", v1, unsafeExtra: new FileSpec("../escaped.txt", Encoding.UTF8.GetBytes("no")));
        var escapeState = Install(escape, escapeInstall, null, out var escapeError);
        check("refuse: an escaping path is rejected",
            escapeError.Length > 0 || escapeState.Incomplete,
            escapeError.Length > 0 ? escapeError : "state flagged incomplete");
        check("refuse: nothing was written outside the install root",
            !File.Exists(Path.Combine(root, "escaped.txt")));

        // 6c. an object the channel lists but the store does not have.
        var missing = Path.Combine(root, "missing");
        var missingInstall = Path.Combine(root, "install-missing");
        WritePack(missing, "1.0.0", v1, omitObjectFor: "scripts/vscripts/r5f_test.nut");
        var missingState = Install(missing, missingInstall, null, out var missingError);
        check("refuse: a missing CAS object fails the install",
            missingError.Length > 0 || missingState.Incomplete,
            missingError.Length > 0 ? missingError : "state flagged incomplete");
        check("refuse: the incomplete file was not committed",
            !File.Exists(Path.Combine(missingInstall, "scripts", "vscripts", "r5f_test.nut")));
        // An aborted install can abandon the files its workers had already
        // started: the object fetch runs in parallel, and the one that fails
        // stops the run without waiting for the others to finish writing. So the
        // honest claim is not "no part files", it is "no part files survive the
        // next install" — the abandoned ones are transient, and nothing broken is
        // ever committed.
        var abandoned = PartLeftovers(missingInstall);

        // The retry against the complete pack must clear them and land the file.
        var afterRetry = Install(channel, missingInstall, null, out var retryError);
        check("refuse: the retry install succeeds", !afterRetry.Incomplete && retryError.Length == 0,
            retryError.Length == 0 ? "ok" : retryError);
        check("refuse: the retry cleared every part file",
            PartLeftovers(missingInstall).Length == 0,
            abandoned.Length == 0
                ? "no part files were abandoned this run"
                : "abandoned " + string.Join(",", abandoned) + " → cleaned");
        check("refuse: the file the store was missing is committed by the retry",
            File.Exists(Path.Combine(missingInstall, "scripts", "vscripts", "r5f_test.nut")));
    }

    /// <summary>Part files under an install root, named so a failure says which
    /// one survived rather than just "not clean".</summary>
    static string[] PartLeftovers(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*" + ContentExecutor.PartSuffix, SearchOption.AllDirectories)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null)
                    .Select(n => n!)
                    .ToArray()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ---------------------------------------------------------------- helpers

    sealed record FileSpec(string Path, byte[] Bytes);

    static void TryDelete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* a locked temp file is not worth failing the run over */ }
    }

    static IEnumerable<string> SafeList(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.GetFileSystemEntries(root).Select(e => Path.GetFileName(e) ?? e).ToArray()
                : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Deterministic non-zero bytes, so a same-size edit is detectable.</summary>
    static byte[] PseudoBytes(int count, byte seed)
    {
        var bytes = new byte[count];
        var x = (uint)(seed * 2654435761u + 1);
        for (var i = 0; i < count; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            bytes[i] = (byte)(x & 0xFF);
        }
        return bytes;
    }

    static bool FileMatches(string install, FileSpec f, out string report)
    {
        var full = Path.Combine(install, f.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) { report = f.Path + " is missing"; return false; }
        var got = File.ReadAllBytes(full);
        if (got.Length != f.Bytes.Length)
        {
            report = $"{f.Path} is {got.Length} bytes, expected {f.Bytes.Length}";
            return false;
        }
        if (!got.AsSpan().SequenceEqual(f.Bytes)) { report = f.Path + " differs"; return false; }
        report = f.Path + " matches";
        return true;
    }

    static bool PackMatches(string install, IReadOnlyList<FileSpec> files, out string report)
    {
        foreach (var f in files)
        {
            var full = Path.Combine(install, f.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) { report = f.Path + " is missing"; return false; }
            var got = File.ReadAllBytes(full);
            if (got.Length != f.Bytes.Length) { report = $"{f.Path} is {got.Length} bytes, expected {f.Bytes.Length}"; return false; }
            if (!got.AsSpan().SequenceEqual(f.Bytes)) { report = f.Path + " differs"; return false; }
        }
        report = $"{files.Count} files match";
        return true;
    }

    static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Writes a channel + content manifest + CAS tree for one pack version. The
    /// content manifest is written first so the channel can carry its real digest;
    /// the tamper option deliberately carries a wrong one.
    /// </summary>
    static string WritePack(
        string channelDir,
        string version,
        IReadOnlyList<FileSpec> files,
        bool tamperManifestSha = false,
        FileSpec? unsafeExtra = null,
        string? omitObjectFor = null)
    {
        Directory.CreateDirectory(channelDir);

        var entries = new List<ContentFile>();
        foreach (var f in files)
        {
            var sha = Sha256Hex(f.Bytes);
            if (!string.Equals(f.Path, omitObjectFor, StringComparison.Ordinal))
            {
                var objDir = Path.Combine(channelDir, "cas", sha[..2]);
                Directory.CreateDirectory(objDir);
                File.WriteAllBytes(Path.Combine(objDir, sha), f.Bytes);
            }
            entries.Add(new ContentFile { Path = f.Path, Size = f.Bytes.Length, Sha256 = sha });
        }

        if (unsafeExtra is not null)
            entries.Add(new ContentFile { Path = unsafeExtra.Path, Size = unsafeExtra.Bytes.Length, Sha256 = Sha256Hex(unsafeExtra.Bytes) });

        // content_hash describes the file list, so it can be computed before the
        // manifest is written (a digest of the file itself would be circular).
        var contentHash = Sha256Hex(Encoding.UTF8.GetBytes(string.Join(
            "\n", entries.Select(e => $"{e.Path}\u0000{e.Size}\u0000{e.Sha256}"))));

        var manifest = new ContentManifest
        {
            ManifestId = "selftest-" + version,
            Preset = "client",
            CatalogVersion = version,
            CreatedUtc = "2026-09-21T00:00:00Z",
            FileCount = entries.Count,
            PayloadBytes = entries.Sum(e => e.Size),
            ObjectCount = entries.Count,
            ContentHash = contentHash,
            Files = entries,
        };

        var manifestPath = Path.Combine(channelDir, "CONTENT_MANIFEST.json");
        ContentManifestIO.Save(manifestPath, manifest);
        var manifestSha = Sha256Hex(File.ReadAllBytes(manifestPath));

        var carriedSha = tamperManifestSha
            ? new string('0', 64)
            : manifestSha;

        var channel = new ChannelManifest
        {
            Schema = 1,
            Kind = "channel_manifest",
            SdkVersion = "R5FSelfTest",
            GateName = "R5FSelfTest",
            Channel = "selftest",
            Prerelease = true,
            MarketingTag = "selftest " + version,
            // base_url is left unset: the loader derives it from the manifest's own
            // directory, which is how a folder-served channel resolves its relatives.
            Client = new ChannelTrackTip
            {
                Preset = "client",
                CatalogVersion = version,
                ContentHash = contentHash,
                TotalBytes = manifest.PayloadBytes,
                ContentManifestUrl = "CONTENT_MANIFEST.json",
                ContentManifestSha256 = carriedSha,
                CasBaseUrl = channelDir,
            },
        };

        ChannelManifestIO.Save(Path.Combine(channelDir, ProductConstants.ChannelManifestFileName), channel);
        return contentHash;
    }

    static ChannelManifest LoadChannel(string channelDir)
    {
        using var fetcher = new FileSystemFetcher();
        // A local channel is the manifest file itself, exactly as a user would
        // point the launcher at a folder-served channel.
        var url = Path.Combine(channelDir, ProductConstants.ChannelManifestFileName);
        return ChannelSource.LoadAsync(url, fetcher, Path.Combine(channelDir, "cache"), CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    static InstallHealthReport Assess(string channelDir, string install)
        => ContentInstallService.Assess(LoadChannel(channelDir), install, requireClient: true, requireServer: false);

    /// <summary>Runs the real installer. A refusal surfaces as either a thrown
    /// exception or a state flagged incomplete, so both are reported.</summary>
    static InstallState Install(
        string channelDir, string install, IProgress<ContentInstallProgress>? progress, out string error)
    {
        error = "";
        try
        {
            var state = ContentInstallService.InstallAsync(
                LoadChannel(channelDir),
                InstallMode.Full,
                install,
                fetcher: null,
                progress: progress,
                cancel: CancellationToken.None,
                runControl: null,
                decideOverlay: null,
                allowContent: true,
                allowPlatform: true,
                keepLocalFiles: false).GetAwaiter().GetResult();

            if (state.Incomplete || !string.IsNullOrWhiteSpace(state.LastError))
                error = state.LastError ?? "state flagged incomplete";
            return state;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return new InstallState();
        }
    }

    sealed class CaptureProgress : IProgress<ContentInstallProgress>
    {
        public int Count;
        public List<string> Phases { get; } = new();

        public void Report(ContentInstallProgress value)
        {
            Interlocked.Increment(ref Count);
            lock (Phases) Phases.Add(value.Phase ?? "");
        }
    }
}
