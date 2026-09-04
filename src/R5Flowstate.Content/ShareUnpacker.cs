using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

public static class ShareUnpacker
{
    public static int Unpack(
        string shareDir,
        string destInstallPath,
        string? password = null,
        IProgress<ContentInstallProgress>? progress = null,
        CancellationToken cancel = default,
        OverlayExtractPolicy overlay = OverlayExtractPolicy.WriteOfficial)
    {
        if (string.IsNullOrWhiteSpace(shareDir))
            throw new ArgumentException("Share directory is required.", nameof(shareDir));
        if (string.IsNullOrWhiteSpace(destInstallPath))
            throw new ArgumentException("Install path is required.", nameof(destInstallPath));

        var manPath = Path.Combine(shareDir, ProductConstants.ShareManifestFileName);
        if (!File.Exists(manPath))
            throw new FileNotFoundException($"missing {manPath}", manPath);

        var man = ShareManifestIO.Load(manPath);
        Directory.CreateDirectory(destInstallPath);

        var engine = (man.Engine ?? string.Empty).Trim();
        if (man.Schema >= 2 || string.Equals(engine, "7z", StringComparison.OrdinalIgnoreCase))
            return Unpack7z(shareDir, destInstallPath, man, password, progress, cancel, overlay);

        return UnpackZip(shareDir, destInstallPath, man, password, progress, cancel);
    }

    static int Unpack7z(
        string shareDir,
        string destInstallPath,
        ShareManifest man,
        string? password,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel,
        OverlayExtractPolicy overlay)
    {
        var seven = SevenZipLocator.Find7z();
        var archives = man.Archives;
        if (archives is null || archives.Count == 0)
        {
            // Fall back: group volumes by archive_base (matches share_unpack.py).
            var byBase = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var vol in man.Volumes)
            {
                var bas = vol.ArchiveBase ?? string.Empty;
                if (string.IsNullOrEmpty(bas))
                    continue;
                if (!byBase.TryGetValue(bas, out var list))
                {
                    list = new List<string>();
                    byBase[bas] = list;
                }
                if (!string.IsNullOrEmpty(vol.Name))
                    list.Add(vol.Name);
            }

            archives = byBase.Select(kv => new ShareArchive
            {
                ArchiveBase = kv.Key,
                Volumes = kv.Value,
                Group = "?",
            }).ToList();
        }

        var count = 0;
        for (var ai = 0; ai < archives.Count; ai++)
        {
            cancel.ThrowIfCancellationRequested();
            var arch = archives[ai];
            var vols = arch.Volumes ?? new List<string>();
            string? first = null;
            if (vols.Count > 0)
            {
                var cand = Path.Combine(shareDir, vols[0]);
                if (File.Exists(cand))
                    first = cand;
            }

            var bas = arch.ArchiveBase ?? string.Empty;
            if (first is null && !string.IsNullOrEmpty(bas))
            {
                var part001 = Path.Combine(shareDir, bas + ".001");
                if (File.Exists(part001))
                    first = part001;
                else
                {
                    var single = Path.Combine(shareDir, bas);
                    if (File.Exists(single))
                        first = single;
                }
            }

            if (first is null || !File.Exists(first))
            {
                progress?.Report(new ContentInstallProgress
                {
                    Phase = "unpack",
                    Current = ai + 1,
                    Total = archives.Count,
                    Message = $"missing archive start for {bas}",
                });
                continue;
            }

            var label = string.IsNullOrWhiteSpace(arch.Group)
                ? Path.GetFileName(first)
                : arch.Group;
            if (ArchiveAlreadyExtracted(destInstallPath, arch, overlay))
            {
                ReportUnpack(progress, ai, archives.Count, label, 100, arch.PayloadBytes);
                count += arch.FileCount;
                continue;
            }

            ReportUnpack(progress, ai, archives.Count, label, 0, arch.PayloadBytes);

            var args = $"x \"{first}\" -o\"{destInstallPath}\" -y -aoa -bsp1 -bb0 -bso0 -mmt=1";
            if (overlay == OverlayExtractPolicy.KeepEdits)
                args += OverlayExcludeArgs();
            if (!string.IsNullOrEmpty(password))
                args += $" -p{password}";

            Run7zExtract(
                seven,
                args,
                destInstallPath,
                ai,
                archives.Count,
                label,
                arch.PayloadBytes,
                progress,
                cancel);

            count += arch.FileCount;
            progress?.Report(new ContentInstallProgress
            {
                Phase = "archives",
                Current = ai + 1,
                Total = archives.Count,
                Message = Path.GetFileName(first),
            });
        }

        return count;
    }

    static string OverlayExcludeArgs()
    {
        var sb = new StringBuilder();
        foreach (var p in OverlayPaths.OverlayOwnedPrefixes)
            sb.Append(" -xr!").Append(p.TrimEnd('/').Replace('/', '\\'));
        foreach (var e in OverlayPaths.OverlayOwnedExact)
            sb.Append(" -x!").Append(e.Replace('/', '\\'));
        return sb.ToString();
    }

    static bool ArchiveAlreadyExtracted(
        string destRoot,
        ShareArchive arch,
        OverlayExtractPolicy overlay)
    {
        var files = arch.Files;
        if (files is null || files.Count == 0)
            return false;
        foreach (var f in files)
        {
            if (string.IsNullOrWhiteSpace(f.Path))
                continue;
            if (overlay == OverlayExtractPolicy.KeepEdits && OverlayPaths.IsOverlayOwned(f.Path))
                continue;
            var rel = f.Path.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.Combine(destRoot, rel);
            try
            {
                if (!File.Exists(full))
                    return false;
                if (f.Size > 0 && new FileInfo(full).Length != f.Size)
                    return false;
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    static readonly Regex s_pct = new(@"(\d+)\s*%", RegexOptions.Compiled);
    static readonly Regex s_extract = new(
        @"(?:Extracting|Extract)\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static void Run7zExtract(
        string seven,
        string args,
        string destInstallPath,
        int archiveIndex,
        int archiveCount,
        string label,
        long payloadBytes,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = seven,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException($"Failed to start 7z: {seven}");
        using var kill = cancel.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }
        });

        var err = new StringBuilder();
        var errTask = Task.Run(() =>
        {
            try
            {
                err.Append(proc.StandardError.ReadToEnd());
            }
            catch
            {
                // process died
            }
        });

        Pump7zStdout(
            proc,
            archiveIndex,
            archiveCount,
            label,
            payloadBytes,
            progress,
            cancel);

        try
        {
            proc.WaitForExit();
            cancel.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }
            throw;
        }

        try { errTask.Wait(500); }
        catch { /* ignore */ }

        if (proc.ExitCode != 0)
        {
            var msg = err.ToString().Trim();
            if (msg.Length > 800)
                msg = msg[..800];
            throw new InvalidOperationException(
                $"7z extract failed ({proc.ExitCode}): {msg}");
        }

        ReportUnpack(
            progress, archiveIndex, archiveCount, label, 100, payloadBytes);
    }

    static void Pump7zStdout(
        Process proc,
        int archiveIndex,
        int archiveCount,
        string label,
        long payloadBytes,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
    {
        var acc = new StringBuilder();
        var buf = new byte[512];
        var stream = proc.StandardOutput.BaseStream;
        var lastUi = DateTime.MinValue;
        var lastPct = -1;
        var file = label;

        void flush()
        {
            var line = acc.ToString().Trim();
            acc.Clear();
            if (line.Length == 0)
                return;

            var m = s_extract.Match(line);
            if (m.Success)
            {
                var name = m.Groups[1].Value.Trim();
                if (name.Length > 0)
                    file = name;
            }

            m = s_pct.Match(line);
            if (!m.Success)
                return;
            if (!int.TryParse(m.Groups[1].Value, out var pct))
                return;
            pct = Math.Clamp(pct, 0, 100);
            var now = DateTime.UtcNow;
            if (pct == lastPct && (now - lastUi).TotalMilliseconds < 120)
                return;
            lastPct = pct;
            lastUi = now;
            ReportUnpack(
                progress, archiveIndex, archiveCount, file, pct, payloadBytes);
        }

        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            int n;
            try
            {
                n = stream.Read(buf, 0, buf.Length);
            }
            catch
            {
                break;
            }

            if (n <= 0)
                break;
            for (var i = 0; i < n; i++)
            {
                var c = (char)buf[i];
                if (c == '\r' || c == '\n')
                    flush();
                else
                    acc.Append(c);
            }
        }

        if (acc.Length > 0)
            flush();
    }

    static void ReportUnpack(
        IProgress<ContentInstallProgress>? progress,
        int archiveIndex,
        int archiveCount,
        string file,
        int pct,
        long payloadBytes)
    {
        long current;
        long total;
        if (payloadBytes >= 1024)
        {
            total = payloadBytes;
            current = (long)(payloadBytes * (pct / 100.0));
        }
        else
        {
            total = 100;
            current = pct;
        }

        progress?.Report(new ContentInstallProgress
        {
            Phase = "unpack",
            Current = current,
            Total = total,
            StepIndex = archiveIndex + 1,
            StepCount = archiveCount,
            FileName = file,
            Message = file,
        });
    }

    static int UnpackZip(
        string shareDir,
        string destInstallPath,
        ShareManifest man,
        string? password,
        IProgress<ContentInstallProgress>? progress,
        CancellationToken cancel)
    {
        // Schema 1 legacy: zip volumes. Password-protected zip is not implemented
        // (shipping path is schema 2 / 7z); empty password only.
        if (!string.IsNullOrEmpty(password))
            throw new NotSupportedException(
                "Password-protected zip volumes are not supported; use schema 2 / 7z packs.");

        var volumes = man.Volumes ?? new List<ShareVolume>();
        var count = 0;
        for (var vi = 0; vi < volumes.Count; vi++)
        {
            cancel.ThrowIfCancellationRequested();
            var vol = volumes[vi];
            var zpath = Path.Combine(shareDir, vol.Name);
            if (!File.Exists(zpath))
            {
                progress?.Report(new ContentInstallProgress
                {
                    Phase = "unpack",
                    Message = $"missing volume: {vol.Name}",
                });
                continue;
            }

            using var zf = ZipFile.OpenRead(zpath);
            var entries = zf.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            for (var i = 0; i < entries.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var entry = entries[i];
                var target = Path.Combine(destInstallPath, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                entry.ExtractToFile(target, overwrite: true);
                count++;
                progress?.Report(new ContentInstallProgress
                {
                    Phase = $"unpack:{vol.Name}",
                    Current = i + 1,
                    Total = entries.Count,
                    Message = entry.FullName,
                });
            }

            progress?.Report(new ContentInstallProgress
            {
                Phase = "volumes",
                Current = vi + 1,
                Total = volumes.Count,
                Message = vol.Name,
            });
        }

        return count;
    }
}
