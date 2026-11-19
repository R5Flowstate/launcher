using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>
/// Review of one package zip for the master server's queue. Runs the same checks an install runs,
/// then lists everything a reviewer has to read. Package content is only unpacked and read; nothing
/// in it is loaded or executed, and nothing is written outside the work directory.
/// </summary>
public static class ModReview
{
    public const int ReportVersion = 1;
    public const int MaxFilesListed = 5000;
    public const int MaxScriptBytesEach = 256 * 1024;
    public const int MaxScriptBytesTotal = 3 * 1024 * 1024;
    public const int MaxRiskHits = 2000;
    public const int MaxJobBytes = 4096;
    public const int MaxJobsPerRun = 50;

    /// <summary>Natives and patterns a reviewer reads every hit of. Extend when natives are added.</summary>
    public static readonly string[] RiskTokens =
    {
        "ModManager_", "LauncherHandoff_", "ClientCommand", "ServerCommand", "SendCommand", "Cbuf",
        "DevTextBuffer", "Remote_Register", "RunUIScript", "HttpRequest", "NSHttp", "PlayerPrefs",
        "Cafe_PlayerPrefs", "SetConVar", "GetConVar", "script_", "printt(",
    };

    static readonly HashSet<string> s_scriptExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nut", ".gnut", ".rson", ".cfg", ".vdf", ".json", ".kv", ".res", ".menu", ".txt", ".csv", ".md",
    };

    static readonly HashSet<string> s_codeExt = new(StringComparer.OrdinalIgnoreCase) { ".nut", ".gnut", ".rson", ".cfg" };

    static readonly string[] s_liveRootFiles =
    {
        "mod.vdf", "manifest.json", "icon.png", "readme.md", "changelog.md", "license", "license.md", "license.txt",
    };

    static readonly string[] s_livePrefixes =
    {
        "scripts/vscripts/", "scripts/weapons/", "scripts/levels/", "paks/win64/", "paks/win64_server/",
    };

    static readonly Regex s_blockOpen = new(
        "\"(ConVars|LocalizationFiles|LocalizationOverrides|DatatableOverrides|DatatableAppends|Maps)\"\\s*\\{\\s*\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Regex s_gameBuilds = new("\"GameBuilds\"\\s*\\{([^}]*)\\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex s_gameBuild = new("\"(s21|s30)\"\\s*\"1\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Regex s_wrapVm = new("\"VM\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly JsonSerializerOptions s_out = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin),
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Reviews <paramref name="zipPath"/> inside <paramref name="workDir"/>, which must be an empty, real directory.</summary>
    public static JsonObject Review(string zipPath, string fullName, string version, string workDir)
    {
        var report = new JsonObject
        {
            ["v"] = ReportVersion,
            ["ok"] = false,
            ["failed_check"] = string.Empty,
            ["message"] = string.Empty,
            ["full_name"] = fullName,
            ["version"] = version,
        };
        var notes = new JsonArray();
        report["notes"] = notes;

        var dash = fullName.IndexOf('-');
        if (!CuratedCatalog.IsFullName(fullName) || !CuratedCatalog.IsVersion(version) || dash <= 0)
            return Fail(report, "arguments", "full name or version is malformed");
        var owner = fullName[..dash];
        var name = fullName[(dash + 1)..];

        if (!IsEmptyRealDirectory(workDir))
            return Fail(report, "arguments", "work dir must be an existing empty directory");

        try
        {
            var info = new FileInfo(zipPath);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return Fail(report, "arguments", "zip is not a regular file");
            report["size"] = info.Length;
            report["sha256"] = HashFile(zipPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(report, "arguments", "zip could not be read: " + ex.Message);
        }

        var staging = Path.Combine(workDir, "x");
        var check = "archive";
        try
        {
            ArchiveStats(zipPath, report);
            var (id, doc, vdfText) = ModInstaller.CheckForReview(zipPath, staging, owner, name, version, c => check = c);
            report["id"] = id;
            report["id_binding_ok"] = true;
            notes.Add("The check that a declared map is not one the game already ships runs at install time.");
            Describe(staging, report, vdfText);
            report["ok"] = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (check == "id_binding")
                report["id_binding_ok"] = false;
            Fail(report, check, ex.Message);
            if (Directory.Exists(staging) && check != "archive")
            {
                try
                {
                    Describe(staging, report, ReadVdf(staging));
                }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    notes.Add("Contents could not be listed after the failed check.");
                }
            }
        }
        finally
        {
            ClearDirectory(workDir);
        }

        return report;
    }

    static JsonObject Fail(JsonObject report, string check, string message)
    {
        report["ok"] = false;
        report["failed_check"] = check;
        report["message"] = Clean(message, 300, keepNewlines: false);
        return report;
    }

    static string ReadVdf(string staging) =>
        SafePath.TryJoin(staging, ModsStore.ModSettingsFileName, out var p) && File.Exists(p)
            ? ModsStore.ReadModSettingsText(p) ?? string.Empty
            : string.Empty;

    static void ArchiveStats(string zipPath, JsonObject report)
    {
        var declared = ModInstaller.DeclaredZipEntryCount(zipPath);
        if (declared > ModInstaller.MaxZipEntries)
            throw new InvalidOperationException("Archive declares " + declared + " entries.");
        using var zip = ZipFile.OpenRead(zipPath);
        long total = 0;
        double worst = 0;
        foreach (var e in zip.Entries)
        {
            total += Math.Max(0, e.Length);
            if (e.CompressedLength > 0)
                worst = Math.Max(worst, (double)e.Length / e.CompressedLength);
        }

        report["entries"] = zip.Entries.Count;
        report["uncompressed"] = total;
        report["max_ratio"] = Math.Round(worst, 2);
    }

    static void Describe(string staging, JsonObject report, string vdfText)
    {
        var files = new JsonArray();
        var scripts = new JsonArray();
        var hits = new JsonArray();
        var rpaks = new JsonArray();
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var scriptBudget = MaxScriptBytesTotal;

        var all = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: Path.GetRelativePath(staging, f).Replace('\\', '/')))
            .OrderBy(f => s_codeExt.Contains(Path.GetExtension(f.Rel)) ? 0 : 1)
            .ThenBy(f => f.Rel, StringComparer.Ordinal)
            .ToList();

        foreach (var (full, rel) in all)
        {
            var size = new FileInfo(full).Length;
            if (files.Count < MaxFilesListed)
                files.Add(new JsonObject { ["path"] = Clean(rel, 260, false), ["size"] = size, ["sha256"] = HashFile(full) });

            var lower = rel.ToLowerInvariant();
            if (!IsLiveEntry(lower))
                reasons.Add(lower.Contains('/') ? lower[..lower.IndexOf('/')] : lower);

            var ext = Path.GetExtension(rel);
            if (ext.Equals(".rpak", StringComparison.OrdinalIgnoreCase) || ext.Equals(".starpak", StringComparison.OrdinalIgnoreCase))
                rpaks.Add(PakHeader(full, rel, size));

            if (!s_scriptExt.Contains(ext))
                continue;
            var text = ReadText(full, MaxScriptBytesEach * 4);
            if (s_codeExt.Contains(ext))
                ScanTokens(rel, text, hits);
            if (scriptBudget <= 0)
                continue;
            var take = Math.Min(Math.Min(text.Length, MaxScriptBytesEach), scriptBudget);
            scriptBudget -= take;
            scripts.Add(new JsonObject
            {
                ["path"] = Clean(rel, 260, false),
                ["lines"] = CountLines(text),
                ["text"] = Clean(text[..take], take, keepNewlines: true),
                ["truncated"] = take < text.Length,
            });
        }

        if (s_blockOpen.Matches(vdfText) is { Count: > 0 } blocks)
        {
            foreach (Match m in blocks)
            {
                reasons.Add(m.Groups[1].Value.ToLowerInvariant() switch
                {
                    "convars" => "settings",
                    "localizationfiles" or "localizationoverrides" => "text",
                    "datatableoverrides" or "datatableappends" => "data tables",
                    _ => "maps",
                });
            }
        }

        foreach (Match m in s_wrapVm.Matches(vdfText))
        {
            if (Regex.IsMatch(m.Groups[1].Value, @"(^|[ ,|])UI($|[ ,|])", RegexOptions.IgnoreCase))
                reasons.Add("menu scripts");
        }

        if (SafePath.TryJoin(staging, "scripts/vscripts/scripts.rson", out var rson) && File.Exists(rson) &&
            CompileListReachesUi(ReadText(rson, 1024 * 1024)))
            reasons.Add("menu scripts");

        report["game_builds"] = new JsonArray(GameBuildsOf(vdfText).Select(b => (JsonNode)JsonValue.Create(b)!).ToArray());
        report["files"] = files;
        report["restart_reasons"] = new JsonArray(reasons.Select(r => (JsonNode)JsonValue.Create(Clean(r, 64, false))!).ToArray());
        report["restart"] = reasons.Count > 0;
        report["scripts"] = scripts;
        report["risk_hits"] = hits;
        report["rpak"] = rpaks;

        if (SafePath.TryJoin(staging, ModsStore.ManifestFileName, out var manPath) && ModsStore.ReadManifest(manPath) is { } man)
        {
            var deps = new JsonArray();
            foreach (var d in man.Dependencies ?? new List<string>())
            {
                if (deps.Count >= CuratedCatalog.MaxDeps)
                    break;
                if (CuratedCatalog.IsDependencyPin(d))
                    deps.Add(d);
            }

            report["manifest"] = new JsonObject
            {
                ["name"] = Clean(man.Name, 64, false),
                ["version_number"] = Clean(man.VersionNumber, 32, false),
                ["description"] = Clean(man.Description, 250, false),
                ["dependencies"] = deps,
            };
        }
    }

    /// <summary>The seasons a mod declares in mod.vdf. Declaring nothing means S21, which every older mod targets.</summary>
    public static IReadOnlyList<string> GameBuildsOf(string vdfText)
    {
        var block = s_gameBuilds.Match(vdfText);
        var builds = block.Success
            ? s_gameBuild.Matches(block.Groups[1].Value).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList()
            : new List<string>();
        return builds.Count > 0 ? builds : new List<string> { "s21" };
    }

    /// <summary>Same allowlist the game uses to decide a mod can apply without a restart.</summary>
    public static bool IsLiveEntry(string rel)
    {
        if (rel == "paks/win64/preload.rson")
            return false;
        if (!rel.Contains('/'))
            return s_liveRootFiles.Contains(rel, StringComparer.Ordinal);
        return s_livePrefixes.Any(p => rel.StartsWith(p, StringComparison.Ordinal));
    }

    /// <summary>A compile list naming the menus VM (a standalone <c>UI</c> token, or any <c>!</c>) needs a restart.</summary>
    public static bool CompileListReachesUi(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '!')
                return true;
            if (text[i] != 'U' || i + 1 >= text.Length || text[i + 1] != 'I')
                continue;
            var left = i == 0 || !char.IsAsciiLetterOrDigit(text[i - 1]);
            var right = i + 2 >= text.Length || !char.IsAsciiLetterOrDigit(text[i + 2]);
            if (left && right)
                return true;
        }

        return false;
    }

    static void ScanTokens(string rel, string text, JsonArray hits)
    {
        var line = 1;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n')
                continue;
            var span = text.AsSpan(start, i - start);
            foreach (var token in RiskTokens)
            {
                if (hits.Count >= MaxRiskHits)
                    return;
                if (span.IndexOf(token, StringComparison.Ordinal) >= 0)
                    hits.Add(new JsonObject { ["path"] = Clean(rel, 260, false), ["line"] = line, ["token"] = token });
            }

            line++;
            start = i + 1;
        }
    }

    static JsonObject PakHeader(string full, string rel, long size)
    {
        var head = new byte[8];
        var n = 0;
        using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            n = fs.Read(head, 0, head.Length);
        var magic = n >= 4 ? Encoding.ASCII.GetString(head, 0, 4) : string.Empty;
        var version = n >= 6 ? BitConverter.ToUInt16(head, 4) : 0;
        var flags = n >= 8 ? BitConverter.ToUInt16(head, 6) : 0;
        var isStar = rel.EndsWith(".starpak", StringComparison.OrdinalIgnoreCase);
        var ok = isStar ? magic == "SRPk" : magic == "RPak" && version is 7 or 8;
        return new JsonObject
        {
            ["path"] = Clean(rel, 260, false),
            ["magic_ok"] = ok,
            ["version"] = version,
            ["flags"] = flags,
            ["size"] = size,
        };
    }

    static string ReadText(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var len = (int)Math.Min(fs.Length, maxBytes);
        var buf = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = fs.Read(buf, read, len - read);
            if (n <= 0)
                break;
            read += n;
        }

        return Encoding.UTF8.GetString(buf, 0, read);
    }

    static int CountLines(string text)
    {
        var n = 1;
        foreach (var c in text)
        {
            if (c == '\n')
                n++;
        }

        return n;
    }

    /// <summary>Drops NUL and control chars (the report is stored as JSONB, which refuses NUL).</summary>
    static string Clean(string? s, int max, bool keepNewlines)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var c in s)
        {
            if (sb.Length >= max)
                break;
            if (keepNewlines && (c == '\n' || c == '\t'))
            {
                sb.Append(c);
                continue;
            }

            if (char.IsControl(c) || char.IsSurrogate(c) || c == '\uFFFD')
                continue;
            sb.Append(c);
        }

        return sb.ToString();
    }

    static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    static bool IsEmptyRealDirectory(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                   !info.EnumerateFileSystemInfos().Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static void ClearDirectory(string dir)
    {
        try
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo d && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    ModsStore.DeleteDirectoryNoReparse(d.FullName);
                else
                    entry.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The spool run clears the work dir again before the next job.
        }
    }

    public static string Serialize(JsonObject report) => report.ToJsonString(s_out);

    /// <summary>
    /// One pass over <c>&lt;spool&gt;/inbox/*.job</c>. Each job gets <c>outbox/&lt;id&gt;.json</c> and its inbox
    /// files are removed, so the path unit that starts this does not fire again for it.
    /// </summary>
    public static int RunSpool(string spoolDir, Action<string> log)
    {
        string inbox, outbox, work;
        try
        {
            inbox = Path.Combine(spoolDir, "inbox");
            outbox = Path.Combine(spoolDir, "outbox");
            work = Path.Combine(spoolDir, "work");
            foreach (var d in new[] { spoolDir, inbox, outbox })
            {
                var info = new DirectoryInfo(d);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    log("spool directory missing or a link: " + d);
                    return 3;
                }
            }

            Directory.CreateDirectory(work);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log("spool unusable: " + ex.Message);
            return 3;
        }

        var jobs = Directory.EnumerateFiles(inbox, "*.job").OrderBy(p => p, StringComparer.Ordinal).Take(MaxJobsPerRun).ToList();
        foreach (var jobPath in jobs)
        {
            var stem = Path.GetFileNameWithoutExtension(jobPath);
            if (!long.TryParse(stem, out var id) || id <= 0 || stem != id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                log("dropping job with a bad name: " + Clean(stem, 40, false));
                TryDelete(jobPath);
                continue;
            }

            var zip = Path.Combine(inbox, id + ".zip");
            JsonObject report;
            try
            {
                report = RunJob(jobPath, zip, id, Path.Combine(work, id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                report = Fail(new JsonObject { ["v"] = ReportVersion, ["ok"] = false }, "job", ex.Message);
            }

            try
            {
                var outPath = Path.Combine(outbox, id + ".json");
                var tmp = outPath + ".part";
                File.WriteAllText(tmp, Serialize(report));
                File.Move(tmp, outPath, overwrite: true);
                log("reviewed " + id + " ok=" + report["ok"]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log("report write failed for " + id + ": " + ex.Message);
            }
            finally
            {
                TryDelete(jobPath);
                TryDelete(zip);
            }
        }

        return 0;
    }

    static JsonObject RunJob(string jobPath, string zip, long id, string workDir)
    {
        var info = new FileInfo(jobPath);
        if (info.Length > MaxJobBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("job file is oversized or a link");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(jobPath));
        var root = doc.RootElement;
        if (root.GetProperty("id").GetInt64() != id)
            throw new InvalidOperationException("job id does not match its file name");
        var fullName = root.GetProperty("full_name").GetString() ?? string.Empty;
        var version = root.GetProperty("version").GetString() ?? string.Empty;
        var sha = root.GetProperty("sha256").GetString() ?? string.Empty;

        if (Directory.Exists(workDir))
            ModsStore.DeleteDirectoryNoReparse(workDir);
        Directory.CreateDirectory(workDir);
        try
        {
            var report = Review(zip, fullName, version, workDir);
            if (report["ok"]?.GetValue<bool>() == true &&
                !string.Equals(report["sha256"]?.GetValue<string>(), sha, StringComparison.Ordinal))
            {
                Fail(report, "hash", "zip bytes differ from the ones the master server downloaded");
            }

            return report;
        }
        finally
        {
            try { ModsStore.DeleteDirectoryNoReparse(workDir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
            // Left for the next run; the job stays visible to the path unit.
        }
    }
}
