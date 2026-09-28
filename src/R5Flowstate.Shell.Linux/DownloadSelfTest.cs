using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using R5Flowstate.Linux.Core;

namespace R5Flowstate.Shell.Linux;

/// <summary>
/// Checks for the download path. The stakes are asymmetric here: a bad transfer
/// that is accepted becomes an .exe run under Proton or a 500 MB runtime that
/// fails at launch, so every rejection path is tested by making the server
/// misbehave on purpose. The transport tests run against a loopback HTTP server,
/// which exercises the real socket/stream/range code without leaving the box.
/// </summary>
static class DownloadSelfTest
{
    public static void Run(Action<string, bool, string> check)
    {
        PeProbeChecks(check);
        EaPageChecks(check);
        GeReleaseChecks(check);
        TarChecks(check);
        TransportChecks(check);
    }

    // ------------------------------------------------------------ EA installer

    static void PeProbeChecks(Action<string, bool, string> check)
    {
        var dir = NewTempDir("pe");
        try
        {
            // A minimal but structurally valid PE: MZ, e_lfanew at 0x3C, PE\0\0.
            var pe = new byte[512];
            pe[0] = (byte)'M';
            pe[1] = (byte)'Z';
            BitConverter.GetBytes(0x80).CopyTo(pe, 0x3C);
            pe[0x80] = (byte)'P';
            pe[0x81] = (byte)'E';
            var pePath = Path.Combine(dir, "good.exe");
            File.WriteAllBytes(pePath, pe);
            Check(check, "pe probe: accepts a valid PE image", EaInstaller.ProbePe(pePath) is null,
                EaInstaller.ProbePe(pePath) ?? "ok");

            // The shape a CDN error or a proxy page arrives in.
            var htmlPath = Path.Combine(dir, "err.exe");
            File.WriteAllText(htmlPath, "<!DOCTYPE html><html><body>404 Not Found</body></html>");
            Check(check, "pe probe: rejects an HTML error page",
                EaInstaller.ProbePe(htmlPath) is not null);

            var shortPath = Path.Combine(dir, "short.exe");
            File.WriteAllBytes(shortPath, new byte[10]);
            Check(check, "pe probe: rejects a truncated body",
                EaInstaller.ProbePe(shortPath) is not null);

            var noSig = (byte[])pe.Clone();
            noSig[0x80] = (byte)'X';
            var noSigPath = Path.Combine(dir, "nosig.exe");
            File.WriteAllBytes(noSigPath, noSig);
            Check(check, "pe probe: rejects a missing PE signature",
                EaInstaller.ProbePe(noSigPath) is not null);

            var badOffset = (byte[])pe.Clone();
            BitConverter.GetBytes(0x7FFFFFFF).CopyTo(badOffset, 0x3C);
            var badOffsetPath = Path.Combine(dir, "badoff.exe");
            File.WriteAllBytes(badOffsetPath, badOffset);
            Check(check, "pe probe: rejects an implausible header offset",
                EaInstaller.ProbePe(badOffsetPath) is not null);

            // IsUsableInstaller gates the cache, so a small bogus file must fail
            // it even though it is a structurally fine PE.
            Check(check, "cache gate: a tiny PE is not a usable installer",
                !EaInstaller.IsUsableInstaller(pePath));
            Check(check, "cache gate: a missing file is not a usable installer",
                !EaInstaller.IsUsableInstaller(Path.Combine(dir, "nope.exe")));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    static void EaPageChecks(Action<string, bool, string> check)
    {
        // Shape of ea.com/ea-app: the real CDN link plus noise around it.
        const string page = """
            <html><head><link href="/static/app.css" rel="stylesheet"></head>
            <body>
              <a href="https://www.ea.com/games">Games</a>
              <a href="https://evil.example.com/EAappInstaller.exe">free installer</a>
              <a href="http://origin-a.akamaihd.net/EA-Desktop-Client-Download/installer-releases/EAappInstaller.exe">insecure</a>
              <a href="https://origin-a.akamaihd.net/EA-Desktop-Client-Download/installer-releases/EAappInstaller.exe?utm=1">Download the EA app</a>
            </body></html>
            """;
        var found = EaInstaller.ResolveUrlFromHtml(page);
        Check(check, "ea page: finds the installer link",
            found == "https://origin-a.akamaihd.net/EA-Desktop-Client-Download/installer-releases/EAappInstaller.exe",
            found ?? "null");
        Check(check, "ea page: ignores a non-allowlisted host",
            found is null || !found.Contains("evil.example"), found ?? "null");
        Check(check, "ea page: ignores plain http",
            found is null || found.StartsWith("https://", StringComparison.Ordinal));
        Check(check, "ea page: returns null without HTML",
            EaInstaller.ResolveUrlFromHtml(null) is null
            && EaInstaller.ResolveUrlFromHtml("") is null);

        // A page that only links a non-installer asset must not yield a URL.
        Check(check, "ea page: no installer link → null",
            EaInstaller.ResolveUrlFromHtml("<a href=\"https://www.ea.com/help\">help</a>") is null);
    }

    // ----------------------------------------------------------- Proton-GE bit

    static void GeReleaseChecks(Action<string, bool, string> check)
    {
        const string hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Check(check, "digest: parses sha256:<hex>",
            ProtonManager.ParseDigest("sha256:" + hex) == hex,
            ProtonManager.ParseDigest("sha256:" + hex) ?? "null");
        Check(check, "digest: rejects another algorithm",
            ProtonManager.ParseDigest("sha512:" + hex) is null);
        Check(check, "digest: rejects a short hash",
            ProtonManager.ParseDigest("sha256:abcdef") is null);
        Check(check, "digest: rejects a non-hex body",
            ProtonManager.ParseDigest("sha256:" + new string('z', 64)) is null);
        Check(check, "digest: rejects empty and null",
            ProtonManager.ParseDigest(null) is null && ProtonManager.ParseDigest("") is null);

        Check(check, "asset name: strips .tar.gz",
            ProtonManager.StripArchiveSuffix("GE-Proton11-7-x86_64.tar.gz") == "GE-Proton11-7-x86_64",
            ProtonManager.StripArchiveSuffix("GE-Proton11-7-x86_64.tar.gz"));
        Check(check, "asset name: strips .tgz",
            ProtonManager.StripArchiveSuffix("GE-Proton11-7.tgz") == "GE-Proton11-7");

        var gz = Path.Combine(NewTempDir("gz"), "x.tar.gz");
        try
        {
            File.WriteAllBytes(gz, new byte[] { 0x1F, 0x8B, 0x08, 0x00 });
            Check(check, "gzip probe: accepts a gzip stream", ProtonManager.ProbeGzip(gz) is null);
            File.WriteAllText(gz, "<html>rate limited</html>");
            Check(check, "gzip probe: rejects an HTML body", ProtonManager.ProbeGzip(gz) is not null);
        }
        finally
        {
            Cleanup(Path.GetDirectoryName(gz)!);
        }
    }

    // ------------------------------------------------------------- extraction

    static void TarChecks(Action<string, bool, string> check)
    {
        var dir = NewTempDir("tar");
        var root = Path.Combine(dir, "compat");
        var tar = Path.Combine(dir, "GE-Proton-test.tar.gz");
        try
        {
            WriteTestTarball(tar);

            var top = ProtonManager.ExtractAsync(tar, root).GetAwaiter().GetResult();
            var topDir = Path.Combine(root, "GE-Proton-test");
            Check(check, "extract: returns the archive's top-level dir",
                Path.GetFullPath(top) == Path.GetFullPath(topDir), top);

            var script = Path.Combine(topDir, "proton");
            Check(check, "extract: the proton script is present", File.Exists(script));
            Check(check, "extract: the proton script is executable",
                File.Exists(script) && IsExecutable(script),
                File.Exists(script) ? Mode(script) : "missing");

            var data = Path.Combine(topDir, "files", "share", "default_pfx", "system.reg");
            Check(check, "extract: nested files land in place", File.Exists(data));
            Check(check, "extract: a data file stays non-executable",
                File.Exists(data) && !IsExecutable(data), File.Exists(data) ? Mode(data) : "missing");

            // The traversal attempt: a symlink whose target climbs out of the tree.
            Check(check, "extract: refuses a symlink escaping the target dir",
                !PathExists(Path.Combine(topDir, "escape")));
            Check(check, "extract: refuses an absolute symlink",
                !PathExists(Path.Combine(topDir, "absolute")));
            Check(check, "extract: keeps an in-tree relative symlink",
                PathExists(Path.Combine(topDir, "link-to-proton")));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>Builds a .tar.gz shaped like a GE-Proton release, including the
    /// two hostile entries a naive extractor follows out of the tree.</summary>
    static void WriteTestTarball(string path)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionMode.Compress);
        using var writer = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false);

        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "GE-Proton-test/"));
        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "GE-Proton-test/files/share/default_pfx/"));

        var script = new PaxTarEntry(TarEntryType.RegularFile, "GE-Proton-test/proton")
        {
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                   | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                   | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\necho proton\n")),
        };
        writer.WriteEntry(script);

        var reg = new PaxTarEntry(TarEntryType.RegularFile,
            "GE-Proton-test/files/share/default_pfx/system.reg")
        {
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
                   | UnixFileMode.OtherRead,
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes("WINE REGISTRY Version 2\n")),
        };
        writer.WriteEntry(reg);

        writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "GE-Proton-test/escape")
        {
            LinkName = "../../../../etc/passwd",
        });
        writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "GE-Proton-test/absolute")
        {
            LinkName = "/etc/passwd",
        });
        writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "GE-Proton-test/link-to-proton")
        {
            LinkName = "proton",
        });
    }

    // -------------------------------------------------------------- transport

    static void TransportChecks(Action<string, bool, string> check)
    {
        var payload = new byte[3 * 1024 * 1024 + 12345];
        var rng = new Random(20260921);
        rng.NextBytes(payload);
        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var server = TestServer.Start(payload);
        if (server is null)
        {
            Check(check, "loopback server started", false, "no free port for HttpListener");
            return;
        }

        var dir = NewTempDir("dl");
        try
        {
            var dest = Path.Combine(dir, "payload.bin");

            // 1. A plain, honest download.
            server.Reset();
            var ok = Run(() => HttpDownload.ToFileAsync(server.Url("payload.bin"), dest, new DownloadOptions
            {
                ExpectedSha256 = expected,
                MinBytes = 1024,
            }));
            Check(check, "download: succeeds and reports ok", ok.Ok, ok.Error ?? "ok");
            Check(check, "download: size matches the payload", ok.Size == payload.Length,
                $"{ok.Size} vs {payload.Length}");
            Check(check, "download: sha256 matches", ok.Sha256 == expected, ok.Sha256);
            Check(check, "download: file is on disk with the right bytes",
                File.Exists(dest) && new FileInfo(dest).Length == payload.Length);
            Check(check, "download: no .part left behind", !File.Exists(dest + ".part"));

            // 2. A second call reuses the valid file instead of re-fetching it.
            server.Reset();
            var cached = Run(() => HttpDownload.ToFileAsync(server.Url("payload.bin"), dest, new DownloadOptions
            {
                ExpectedSha256 = expected,
                MinBytes = 1024,
            }));
            Check(check, "download: reuses a valid cached file",
                cached.Ok && cached.Cached, cached.Error ?? $"cached={cached.Cached}");
            Check(check, "download: the cached hit made no request", server.Requests == 0,
                $"{server.Requests} request(s)");

            // 3. Resume: a half-written .part is continued with a Range request.
            var resumed = Path.Combine(dir, "resumed.bin");
            var partPath = resumed + ".part";
            const int half = 1_500_000;
            File.WriteAllBytes(partPath, payload.AsSpan(0, half).ToArray());
            server.Reset();
            var r = Run(() => HttpDownload.ToFileAsync(server.Url("payload.bin"), resumed, new DownloadOptions
            {
                ExpectedSha256 = expected,
                MinBytes = 1024,
            }));
            Check(check, "resume: completes from a partial file", r.Ok, r.Error ?? "ok");
            Check(check, "resume: reports that it resumed", r.Resumed, $"resumed={r.Resumed}");
            Check(check, "resume: server was asked for the remaining range",
                server.LastRangeHeader is not null, server.LastRangeHeader ?? "no Range header");
            Check(check, "resume: the file is byte-identical, not doubled",
                File.Exists(resumed) && new FileInfo(resumed).Length == payload.Length,
                File.Exists(resumed) ? new FileInfo(resumed).Length.ToString() : "missing");

            // 4. A server that ignores Range must not produce a corrupt append.
            var noRange = Path.Combine(dir, "norange.bin");
            File.WriteAllBytes(noRange + ".part", payload.AsSpan(0, half).ToArray());
            server.Reset();
            server.SupportRange = false;
            var ig = Run(() => HttpDownload.ToFileAsync(server.Url("payload.bin"), noRange, new DownloadOptions
            {
                ExpectedSha256 = expected,
                MinBytes = 1024,
            }));
            server.SupportRange = true;
            Check(check, "resume: a Range-ignoring server still yields the right file",
                ig.Ok && new FileInfo(noRange).Length == payload.Length, ig.Error ?? "ok");

            // 5. Wrong hash — the poison case. Nothing may be kept.
            var poison = Path.Combine(dir, "poison.bin");
            server.Reset();
            var bad = Run(() => HttpDownload.ToFileAsync(server.Url("payload.bin"), poison, new DownloadOptions
            {
                ExpectedSha256 = new string('a', 64),
                MinBytes = 1024,
            }));
            Check(check, "hash mismatch: fails", !bad.Ok);
            Check(check, "hash mismatch: says why",
                bad.Error?.Contains("sha256 mismatch", StringComparison.Ordinal) == true, bad.Error ?? "");
            Check(check, "hash mismatch: nothing is left on disk",
                !File.Exists(poison) && !File.Exists(poison + ".part"));
            Check(check, "hash mismatch: not retried pointlessly", server.Requests == 1,
                $"{server.Requests} request(s)");

            // 6. A short body (the CDN error-page case) is rejected.
            var shortDest = Path.Combine(dir, "short.bin");
            server.Reset();
            var tiny = Run(() => HttpDownload.ToFileAsync(server.Url("payload.bin"), shortDest,
                new DownloadOptions { MinBytes = payload.Length + 1 }));
            Check(check, "short body: fails the size floor", !tiny.Ok);
            Check(check, "short body: nothing is left on disk",
                !File.Exists(shortDest) && !File.Exists(shortDest + ".part"));

            // 7. A caller-supplied probe can veto the bytes (the PE check's role).
            var probed = Path.Combine(dir, "probed.bin");
            server.Reset();
            var veto = Run(() => HttpDownload.ToFileAsync(server.Url("payload.bin"), probed,
                new DownloadOptions
                {
                    MinBytes = 1024,
                    Validate = _ => "not a Windows executable",
                }));
            Check(check, "probe: a vetoing probe fails the download", !veto.Ok);
            Check(check, "probe: the vetoed body is not kept",
                !File.Exists(probed) && !File.Exists(probed + ".part"));

            // 8. A 404 is a failure, not an empty file.
            var missing = Path.Combine(dir, "missing.bin");
            server.Reset();
            var notFound = Run(() => HttpDownload.ToFileAsync(server.Url("absent.bin"), missing,
                new DownloadOptions { MinBytes = 1, Retries = 0 }));
            Check(check, "http 404: fails", !notFound.Ok, notFound.Error ?? "ok");
            Check(check, "http 404: no file is created", !File.Exists(missing));

            // 9. Non-https off-box is refused before any socket is opened.
            var refused = Run(() => HttpDownload.ToFileAsync("http://example.com/x.exe",
                Path.Combine(dir, "plain.bin"), new DownloadOptions { MinBytes = 1 }));
            Check(check, "policy: plain http to a remote host is refused",
                !refused.Ok && refused.Error?.Contains("non-https", StringComparison.Ordinal) == true,
                refused.Error ?? "");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ----------------------------------------------------------------- helpers

    static DownloadResult Run(Func<Task<DownloadResult>> f)
    {
        try
        {
            return f().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return DownloadResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    static void Check(Action<string, bool, string> check, string name, bool ok, string detail = "")
        => check(name, ok, detail);

    static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(),
            $"r5f-dl-{tag}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    static bool PathExists(string path)
        => File.Exists(path) || Directory.Exists(path) || IsSymlink(path);

    static bool IsSymlink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    static string Mode(string path)
    {
        if (OperatingSystem.IsWindows())
            return "n/a on windows";
        try { return File.GetUnixFileMode(path).ToString(); }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    /// <summary>A loopback HTTP server that can be told to misbehave: ignore
    /// Range, or answer with a body that does not match its hash.</summary>
    sealed class TestServer : IDisposable
    {
        readonly HttpListener _listener = new();
        readonly CancellationTokenSource _cts = new();
        readonly Task _loop;
        int _requests;

        public byte[] Payload { get; }
        public int Port { get; }
        public bool SupportRange { get; set; } = true;
        public string? LastRangeHeader { get; private set; }
        public int Requests => Volatile.Read(ref _requests);

        TestServer(byte[] payload, int port)
        {
            Payload = payload;
            Port = port;
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public static TestServer? Start(byte[] payload)
        {
            for (var port = 38421; port < 38461; port++)
            {
                try
                {
                    return new TestServer(payload, port);
                }
                catch (HttpListenerException)
                {
                    // Port busy — try the next one.
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return null;
        }

        public string Url(string leaf) => $"http://127.0.0.1:{Port}/{leaf}";

        public void Reset()
        {
            Volatile.Write(ref _requests, 0);
            LastRangeHeader = null;
        }

        async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return; // listener disposed
                }

                try
                {
                    Interlocked.Increment(ref _requests);
                    var range = ctx.Request.Headers["Range"];
                    LastRangeHeader = range;

                    if (!ctx.Request.Url!.AbsolutePath.Contains("payload", StringComparison.Ordinal))
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        continue;
                    }

                    var start = 0L;
                    if (SupportRange && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
                    {
                        var spec = range[6..].Split('-')[0];
                        if (long.TryParse(spec, out var from) && from >= 0 && from < Payload.Length)
                            start = from;
                    }

                    var body = Payload.AsSpan((int)start).ToArray();
                    ctx.Response.ContentType = "binary/octet-stream";
                    ctx.Response.ContentLength64 = body.Length;
                    if (start > 0)
                    {
                        ctx.Response.StatusCode = 206;
                        ctx.Response.AddHeader("Content-Range",
                            $"bytes {start}-{Payload.Length - 1}/{Payload.Length}");
                    }
                    await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
