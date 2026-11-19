using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using Xunit;

namespace R5Flowstate.ModToolTests;

/// <summary>Tool state is static; these run one at a time.</summary>
[CollectionDefinition("curated", DisableParallelization = true)]
public sealed class CuratedCollection
{
}

[Collection("curated")]
public sealed class CuratedTests : IDisposable
{
    static readonly RSA s_key = RSA.Create(3072);
    static readonly RSA s_other = RSA.Create(3072);
    static readonly DateTimeOffset s_now = new(2026, 10, 16, 18, 0, 0, TimeSpan.Zero);

    readonly string _root;

    public CuratedTests()
    {
        _root = Path.Combine(@"G:\claude-scratch\mods-apply\work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "r5apex.exe"), Array.Empty<byte>());
        ModTool.ModTool.Keys = new[] { new CuratedKey("cur1", s_key.ExportSubjectPublicKeyInfoPem()) };
        ModTool.ModTool.Clock = () => s_now;
        ModTool.ModTool.CuratedUrl = "https://play.r5flowstate.org/mods/curated.json";
    }

    public void Dispose()
    {
        ModTool.ModTool.Keys = CuratedCatalog.PinnedKeys;
        ModTool.ModTool.Clock = () => DateTimeOffset.UtcNow;
        ModTool.ModTool.CuratedHandler = () => null;
        ModTool.ModTool.NewThunderstore = () => new ThunderstoreClient();
        try { Directory.Delete(_root, recursive: true); } catch { /* scratch */ }
    }

    static JsonObject Version(string v, string sha, long size, params string[] deps) => new()
    {
        ["version"] = v,
        ["sha256"] = sha,
        ["size"] = size,
        ["download_url"] = "",
        ["approved_at"] = "2026-10-16T17:00:00Z",
        ["restart"] = true,
        ["deps"] = new JsonArray(deps.Select(d => (JsonNode)d).ToArray()),
    };

    static JsonObject Package(string owner, string name, params JsonObject[] versions)
    {
        foreach (var v in versions)
            v["download_url"] = CuratedCatalog.CanonicalDownloadUrl(owner, name, v["version"]!.GetValue<string>());
        return new JsonObject
        {
            ["full_name"] = owner + "-" + name,
            ["owner"] = owner,
            ["name"] = name,
            ["official"] = owner == "CafeFPS",
            ["description"] = "desc",
            ["versions"] = new JsonArray(versions.Select(v => (JsonNode)v).ToArray()),
        };
    }

    static byte[] Payload(long serial, DateTimeOffset expires, params JsonObject[] packages) =>
        Payload(serial, expires.AddHours(-72), expires, new JsonArray(), packages);

    static byte[] Payload(long serial, DateTimeOffset issued, DateTimeOffset expires, JsonArray revoked, params JsonObject[] packages) =>
        Encoding.UTF8.GetBytes(new JsonObject
        {
            ["v"] = 1,
            ["serial"] = serial,
            ["issued_at"] = issued.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["expires_at"] = expires.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["community"] = "r5flowstate",
            ["packages"] = new JsonArray(packages.Select(p => (JsonNode)p).ToArray()),
            ["revoked"] = revoked,
        }.ToJsonString());

    static JsonArray Revoked(string full, string version) =>
        new(new JsonObject { ["full_name"] = full, ["version"] = version, ["reason"] = "r" });

    static byte[] Envelope(byte[] payload, RSA? key = null, string keyId = "cur1")
    {
        var sig = (key ?? s_key).SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Encoding.UTF8.GetBytes(new JsonObject
        {
            ["v"] = 1,
            ["key_id"] = keyId,
            ["payload"] = Convert.ToBase64String(payload),
            ["sig"] = Convert.ToBase64String(sig),
        }.ToJsonString());
    }

    static IReadOnlyList<CuratedKey> Keys => new[] { new CuratedKey("cur1", s_key.ExportSubjectPublicKeyInfoPem()) };

    static string Code(Action a)
    {
        var ex = Assert.Throws<CuratedListException>(a);
        return ex.Code;
    }

    [Fact]
    public void PinnedKey_IsAUsableRsa3072Key()
    {
        var pinned = CuratedCatalog.PinnedKeys.Single(k => k.KeyId == "cur1");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pinned.PublicPem);
        Assert.True(rsa.KeySize >= CuratedCatalog.MinKeyBits);
    }

    [Fact]
    public void GoodSignature_Verifies()
    {
        var env = Envelope(Payload(3, s_now.AddDays(1), Package("Ann", "A", Version("1.0.0", new string('a', 64), 5))));
        var list = CuratedCatalog.Verify(env, Keys, s_now, 0);
        Assert.Equal(3, list.Serial);
        Assert.Equal("1.0.0", list.Find("Ann-A")!.Newest.Version);
    }

    [Fact]
    public void EachSeason_SeesOnlyItsOwnPackages()
    {
        var doc = JsonNode.Parse(Payload(3, s_now.AddDays(1), Package("Ann", "Old", Version("1.0.0", new string('a', 64), 5))))!.AsObject();
        var plain = CuratedCatalog.Verify(Envelope(Encoding.UTF8.GetBytes(doc.ToJsonString())), Keys, s_now, 0);
        Assert.Empty(plain.ForBuild(GameBuild.S30).Packages);

        doc["packages_s30"] = new JsonArray(Package("Ann", "New", Version("1.0.0", new string('b', 64), 5)));
        var list = CuratedCatalog.Verify(Envelope(Encoding.UTF8.GetBytes(doc.ToJsonString())), Keys, s_now, 0);
        Assert.NotNull(list.ForBuild(GameBuild.S21).Find("Ann-Old"));
        Assert.Null(list.ForBuild(GameBuild.S21).Find("Ann-New"));
        Assert.NotNull(list.ForBuild(GameBuild.S30).Find("Ann-New"));
        Assert.Null(list.ForBuild(GameBuild.S30).Find("Ann-Old"));
    }

    [Theory]
    [InlineData("\"id\" \"A.B\"", "s21")]
    [InlineData("\"GameBuilds\" { \"s30\" \"1\" }", "s30")]
    [InlineData("\"GameBuilds\"\n{\n\t\"s21\" \"1\"\n\t\"s30\" \"1\"\n}", "s21,s30")]
    [InlineData("\"GameBuilds\" { \"s99\" \"1\" }", "s21")]
    public void GameBuilds_ComeFromModVdf(string vdf, string expected)
    {
        Assert.Equal(expected, string.Join(",", ModReview.GameBuildsOf(vdf)));
    }

    [Theory]
    [InlineData("R5FlowstateSDK005", GameBuild.S30, "R5FlowstateSDK005-s30")]
    [InlineData("R5FlowstateSDK005-s30", GameBuild.S30, "R5FlowstateSDK005-s30")]
    [InlineData("R5FlowstateSDK005-s30", GameBuild.S21, "R5FlowstateSDK005")]
    [InlineData("R5FlowstateSDK005-pt", GameBuild.S21, "R5FlowstateSDK005-pt")]
    public void ServerGate_FollowsTheSeason(string wire, GameBuild build, string expected)
    {
        Assert.Equal(expected, VersionIdentity.WireForBuild(wire, build));
    }

    [Fact]
    public void WrongKey_Tampered_Expired_Rollback_AreRefused()
    {
        var payload = Payload(3, s_now.AddDays(1), Package("Ann", "A", Version("1.0.0", new string('a', 64), 5)));
        Assert.Equal("bad_signature", Code(() => CuratedCatalog.Verify(Envelope(payload, s_other), Keys, s_now, 0)));
        Assert.Equal("unknown_key", Code(() => CuratedCatalog.Verify(Envelope(payload, keyId: "cur9"), Keys, s_now, 0)));

        var env = JsonNode.Parse(Envelope(payload))!.AsObject();
        var tampered = (byte[])payload.Clone();
        tampered[tampered.Length / 2] ^= 0x01;
        env["payload"] = Convert.ToBase64String(tampered);
        Assert.Equal("bad_signature", Code(() => CuratedCatalog.Verify(Encoding.UTF8.GetBytes(env.ToJsonString()), Keys, s_now, 0)));

        var expired = Envelope(Payload(3, s_now.AddMinutes(-1), Package("Ann", "A", Version("1.0.0", new string('a', 64), 5))));
        Assert.Equal("expired", Code(() => CuratedCatalog.Verify(expired, Keys, s_now, 0)));

        Assert.Equal("serial_rollback", Code(() => CuratedCatalog.Verify(Envelope(payload), Keys, s_now, 4)));
    }

    [Fact]
    public void OverlongLifetime_AndFutureIssue_AreRefused()
    {
        var pkg = Package("Ann", "A", Version("1.0.0", new string('a', 64), 5));
        var longLived = Envelope(Payload(1, s_now.AddHours(-1), s_now.AddDays(30), new JsonArray(), pkg));
        Assert.Equal("bad_payload", Code(() => CuratedCatalog.Verify(longLived, Keys, s_now, 0)));

        pkg = Package("Ann", "A", Version("1.0.0", new string('a', 64), 5));
        var future = Envelope(Payload(1, s_now.AddDays(10), s_now.AddDays(13), new JsonArray(), pkg));
        Assert.Equal("bad_payload", Code(() => CuratedCatalog.Verify(future, Keys, s_now, 0)));
    }

    [Fact]
    public void OffHostDownloadUrl_IsRefusedEvenWhenSigned()
    {
        var pkg = Package("Ann", "A", Version("1.0.0", new string('a', 64), 5));
        pkg["versions"]![0]!["download_url"] = "https://evil.example/a.zip";
        var env = Envelope(Payload(1, s_now.AddDays(1), pkg));
        Assert.Equal("bad_payload", Code(() => CuratedCatalog.Verify(env, Keys, s_now, 0)));
    }

    [Fact]
    public async Task CacheFallback_UntilExpiry()
    {
        var env = Envelope(Payload(5, s_now.AddDays(1), Package("Ann", "A", Version("1.0.0", new string('a', 64), 5))));
        var list = await CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None, new FixedHandler(env), Keys, () => s_now);
        Assert.Equal(5, list.Serial);
        Assert.Equal(5, CuratedCatalog.ReadSerial(_root));

        var offline = await CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None, new FailingHandler(), Keys, () => s_now);
        Assert.Equal(5, offline.Serial);

        var later = s_now.AddDays(2);
        var ex = await Assert.ThrowsAsync<CuratedListException>(() =>
            CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None, new FailingHandler(), Keys, () => later));
        Assert.Equal("catalog_unavailable", ex.Code);

        var old = Envelope(Payload(4, s_now.AddDays(1), Package("Ann", "A", Version("1.0.0", new string('a', 64), 5))));
        var rolled = await CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None, new FixedHandler(old), Keys, () => s_now);
        Assert.Equal(5, rolled.Serial);
    }

    [Fact]
    public void Catalog_ListsOnlyCuratedPackages()
    {
        PlantListing(_root, ("Ann", "A", "1.0.0"), ("Evil", "Lookalike", "9.9.9"));
        var env = Envelope(Payload(1, s_now.AddDays(1), Package("Ann", "A", Version("1.0.0", new string('a', 64), 5))));
        ModTool.ModTool.CuratedHandler = () => new FixedHandler(env);

        var sw = new StringWriter();
        Assert.Equal(0, ModTool.ModTool.Run(new[] { "catalog", "--install-root", _root, "--refresh" }, sw));
        var rows = Lines(sw).Where(l => l.Contains("\"ev\":\"pkg\"")).ToList();
        Assert.Single(rows);
        Assert.Contains("\"full_name\":\"Ann-A\"", rows[0]);
        Assert.Contains("\"verified\":true", rows[0]);
        Assert.DoesNotContain(Lines(sw), l => l.Contains("Lookalike"));
    }

    [Fact]
    public void Catalog_Unavailable_ListsNothing()
    {
        PlantListing(_root, ("Ann", "A", "1.0.0"));
        ModTool.ModTool.CuratedHandler = () => new FailingHandler();
        var sw = new StringWriter();
        Assert.Equal(4, ModTool.ModTool.Run(new[] { "catalog", "--install-root", _root, "--refresh" }, sw));
        Assert.DoesNotContain(Lines(sw), l => l.Contains("\"ev\":\"pkg\""));
        Assert.Contains("\"code\":\"catalog_unavailable\"", Lines(sw).Last());
    }

    [Fact]
    public void Plan_RefusesUncuratedRootAndDependency()
    {
        var env = Envelope(Payload(1, s_now.AddDays(1),
            Package("Ann", "A", Version("1.0.0", new string('a', 64), 5, "Bob-Lib-1.0.0"))));
        ModTool.ModTool.CuratedHandler = () => new FixedHandler(env);

        var sw = new StringWriter();
        Assert.Equal(3, ModTool.ModTool.Run(new[] { "plan", "--install-root", _root, "--root", "Evil-Thing" }, sw));
        Assert.Contains("\"code\":\"not_in_catalog\"", Lines(sw).Last());

        sw = new StringWriter();
        Assert.Equal(3, ModTool.ModTool.Run(new[] { "plan", "--install-root", _root, "--root", "Ann-A" }, sw));
        Assert.Contains("\"code\":\"dependency_unverified\"", Lines(sw).Last());
    }

    [Fact]
    public void Plan_DependencyPinIsAMinimum()
    {
        var env = Envelope(Payload(1, s_now.AddDays(1),
            Package("Ann", "A", Version("1.0.0", new string('a', 64), 5, "Bob-Lib-1.0.0")),
            Package("Bob", "Lib", Version("1.2.0", new string('b', 64), 5))));
        ModTool.ModTool.CuratedHandler = () => new FixedHandler(env);
        var sw = new StringWriter();
        Assert.Equal(0, ModTool.ModTool.Run(new[] { "plan", "--install-root", _root, "--root", "Ann-A" }, sw));
        Assert.Contains(Lines(sw), l => l.Contains("\"pin\":\"Bob-Lib-1.2.0\""));
    }

    [Fact]
    public void DigestMismatch_RefusesWithoutWriting()
    {
        var env = Envelope(Payload(1, s_now.AddDays(1), Package("Ann", "A", Version("1.0.0", new string('a', 64), 5))));
        ModTool.ModTool.CuratedHandler = () => new FixedHandler(env);
        var digest = PlanDigest("Ann-A");
        var wrong = digest == new string('0', 64) ? new string('a', 64) : new string('0', 64);
        var sw = new StringWriter();
        Assert.Equal(3, ModTool.ModTool.Run(new[] { "stage", "--install-root", _root, "--digest", wrong, "--root", "Ann-A" }, sw));
        Assert.Contains("\"code\":\"plan_changed\"", Lines(sw).Last());
        Assert.False(Directory.Exists(Path.Combine(_root, ".r5f", "staged")));
        Assert.False(Directory.Exists(Path.Combine(_root, "mods")));
    }

    [Fact]
    public void Stage_HashMismatch_ExtractsNothing()
    {
        var zip = ModZip("Ann", "A", "1.0.0");
        var env = Envelope(Payload(1, s_now.AddDays(1), Package("Ann", "A", Version("1.0.0", new string('c', 64), zip.Length))));
        ModTool.ModTool.CuratedHandler = () => new FixedHandler(env);
        ModTool.ModTool.NewThunderstore = () => new ThunderstoreClient(null, new FixedHandler(zip));

        var digest = PlanDigest("Ann-A");
        var sw = new StringWriter();
        Assert.Equal(3, ModTool.ModTool.Run(new[] { "stage", "--install-root", _root, "--digest", digest, "--root", "Ann-A" }, sw));
        Assert.Contains("\"code\":\"hash_mismatch\"", Lines(sw).Last());
        var staged = Path.Combine(_root, ".r5f", "staged");
        Assert.False(Directory.Exists(Path.Combine(staged, "Ann-A")));
        Assert.Empty(Directory.Exists(staged) ? Directory.EnumerateFileSystemEntries(staged) : Array.Empty<string>());
        Assert.False(File.Exists(Path.Combine(_root, ".r5f", "verified.json")));
    }

    [Fact]
    public void Stage_MatchingHash_StagesAndRecordsVerified()
    {
        var zip = ModZip("Ann", "A", "1.0.0");
        var sha = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        var env = Envelope(Payload(1, s_now.AddDays(1), Package("Ann", "A", Version("1.0.0", sha, zip.Length))));
        ModTool.ModTool.CuratedHandler = () => new FixedHandler(env);
        ModTool.ModTool.NewThunderstore = () => new ThunderstoreClient(null, new FixedHandler(zip));

        var digest = PlanDigest("Ann-A");
        var sw = new StringWriter();
        Assert.Equal(0, ModTool.ModTool.Run(new[] { "stage", "--install-root", _root, "--digest", digest, "--root", "Ann-A" }, sw));
        var staged = Path.Combine(_root, ".r5f", "staged", "Ann-A");
        Assert.True(File.Exists(Path.Combine(staged, "mod.vdf")));

        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        Directory.Move(staged, Path.Combine(_root, "mods", "Ann-A"));
        sw = new StringWriter();
        Assert.Equal(0, ModTool.ModTool.Run(new[] { "installed", "--install-root", _root }, sw));
        var row = Lines(sw).Single(l => l.Contains("\"ev\":\"mod\""));
        Assert.Contains("\"verified\":true", row);
        Assert.Contains("\"revoked\":false", row);

        File.AppendAllText(Path.Combine(_root, "mods", "Ann-A", "readme.md"), "edited");
        sw = new StringWriter();
        ModTool.ModTool.Run(new[] { "installed", "--install-root", _root }, sw);
        Assert.Contains("\"verified\":false", Lines(sw).Single(l => l.Contains("\"ev\":\"mod\"")));
    }

    string StageAndApply(byte[] zip, string sha)
    {
        var env = Envelope(Payload(1, s_now.AddDays(1), Package("Ann", "A", Version("1.0.0", sha, zip.Length))));
        ModTool.ModTool.CuratedHandler = () => new FixedHandler(env);
        ModTool.ModTool.NewThunderstore = () => new ThunderstoreClient(null, new FixedHandler(zip));
        var digest = PlanDigest("Ann-A");
        Assert.Equal(0, ModTool.ModTool.Run(new[] { "stage", "--install-root", _root, "--digest", digest, "--root", "Ann-A" }, new StringWriter()));
        Directory.CreateDirectory(Path.Combine(_root, "mods"));
        var dir = Path.Combine(_root, "mods", "Ann-A");
        Directory.Move(Path.Combine(_root, ".r5f", "staged", "Ann-A"), dir);
        return dir;
    }

    string InstalledRow()
    {
        var sw = new StringWriter();
        ModTool.ModTool.Run(new[] { "installed", "--install-root", _root }, sw);
        return Lines(sw).Single(l => l.Contains("\"ev\":\"mod\""));
    }

    [Fact]
    public void Installed_SameSizeEditWithRestoredTime_LosesBadge_IdenticalCopyKeepsIt()
    {
        var zip = ModZip("Ann", "A", "1.0.0");
        var dir = StageAndApply(zip, Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant());
        Assert.Contains("\"verified\":true", InstalledRow());

        var script = Directory.EnumerateFiles(dir, "*.nut", SearchOption.AllDirectories).Single();
        var original = File.ReadAllBytes(script);
        var when = File.GetLastWriteTimeUtc(script);
        var edited = (byte[])original.Clone();
        var at = Encoding.ASCII.GetString(edited).IndexOf("ClientCommand", StringComparison.Ordinal);
        edited[at] = (byte)'X';
        File.WriteAllBytes(script, edited);
        File.SetLastWriteTimeUtc(script, when);
        Assert.Contains("\"verified\":false", InstalledRow());

        File.WriteAllBytes(script, original);
        Assert.Contains("\"verified\":true", InstalledRow());
    }

    [Fact]
    public void Installed_NoCheckableList_NotVerified_LastKnownRevokeHolds()
    {
        var zip = ModZip("Ann", "A", "1.0.0");
        var sha = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        StageAndApply(zip, sha);

        var revoking = Envelope(Payload(2, s_now.AddHours(-1), s_now.AddHours(1), Revoked("Ann-A", "1.0.0")));
        Assert.Equal(2, CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None, new FixedHandler(revoking), Keys,
            () => s_now).Result.Serial);

        ModTool.ModTool.Clock = () => s_now.AddDays(1);
        ModTool.ModTool.CuratedHandler = () => new FailingHandler();
        var row = InstalledRow();
        Assert.Contains("\"verified\":false", row);
        Assert.Contains("\"revoked\":true", row);
    }

    [Fact]
    public void Revoke_TurnsInstalledCopyOff()
    {
        var zip = ModZip("Ann", "A", "1.0.0");
        StageAndApply(zip, Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant());
        var id = ModsStore.Discover(_root).Single().Id;
        ModsStore.SetEnabled(_root, id, true);
        Assert.True(ModsStore.Discover(_root).Single().Enabled);

        var log = new List<string>();
        var revoking = Envelope(Payload(2, s_now.AddHours(-1), s_now.AddHours(1), Revoked("Ann-A", "1.0.0")));
        CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None, new FixedHandler(revoking), Keys,
            () => s_now, log: log.Add).Wait();
        Assert.False(ModsStore.Discover(_root).Single().Enabled);
        Assert.Contains(log, l => l.Contains("turned off by review") && l.Contains(id));
        Assert.Empty(ModRevocation.Usable(ModsStore.Discover(_root), CuratedCatalog.LoadLastKnown(_root, Keys, () => s_now)));
    }

    [Fact]
    public void Installed_EditedFolder_HashedOncePerEdit()
    {
        var zip = ModZip("Ann", "A", "1.0.0");
        var dir = StageAndApply(zip, Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant());
        var verified = Path.Combine(_root, ".r5f", "verified.json");
        var script = Directory.EnumerateFiles(dir, "*.nut", SearchOption.AllDirectories).Single();
        var original = File.ReadAllBytes(script);

        File.AppendAllText(script, "// edited");
        Assert.Contains("\"verified\":false", InstalledRow());
        Assert.Contains("\"modified\"", File.ReadAllText(verified));
        Assert.Contains("\"verified\":false", InstalledRow());

        File.WriteAllBytes(script, original);
        Assert.Contains("\"verified\":true", InstalledRow());
        Assert.DoesNotContain("\"modified\"", File.ReadAllText(verified));
        Assert.Contains("\"verified\":true", InstalledRow());
    }

    [Fact]
    public void Review_RefusesNonEmptyWork_AndStaysInside()
    {
        var zipPath = Path.Combine(_root, "in.zip");
        File.WriteAllBytes(zipPath, ModZip("Ann", "A", "1.0.0"));
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "keep.txt"), "x");

        var refused = ModReview.Review(zipPath, "Ann-A", "1.0.0", work);
        Assert.False(refused["ok"]!.GetValue<bool>());
        Assert.Equal("arguments", refused["failed_check"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(work, "keep.txt")));

        File.Delete(Path.Combine(work, "keep.txt"));
        var before = Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).ToHashSet();
        var report = ModReview.Review(zipPath, "Ann-A", "1.0.0", work);
        Assert.True(report["ok"]!.GetValue<bool>(), report.ToJsonString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(work));
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).ToHashSet());
        Assert.Contains("ClientCommand", report["risk_hits"]!.ToJsonString());
        Assert.False(report["restart"]!.GetValue<bool>());
    }

    [Fact]
    public void Review_FlagsIdThatDoesNotMatchTheTeam()
    {
        var zipPath = Path.Combine(_root, "in.zip");
        File.WriteAllBytes(zipPath, ModZip("Kendell", "AlphaBeast", "1.0.0"));
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(work);
        var report = ModReview.Review(zipPath, "R5flowstate-AlphaBeast", "1.0.0", work);
        Assert.False(report["ok"]!.GetValue<bool>());
        Assert.Equal("id_binding", report["failed_check"]!.GetValue<string>());
        Assert.False(report["id_binding_ok"]!.GetValue<bool>());
    }

    [Fact]
    public void Spool_ProcessesJobAndClearsInbox()
    {
        var spool = Path.Combine(_root, "spool");
        foreach (var d in new[] { "inbox", "outbox" })
            Directory.CreateDirectory(Path.Combine(spool, d));
        var zip = ModZip("Ann", "A", "1.0.0");
        File.WriteAllBytes(Path.Combine(spool, "inbox", "7.zip"), zip);
        var sha = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
        File.WriteAllText(Path.Combine(spool, "inbox", "7.job"),
            "{\"id\":7,\"full_name\":\"Ann-A\",\"version\":\"1.0.0\",\"sha256\":\"" + sha + "\"}");
        File.WriteAllText(Path.Combine(spool, "inbox", "../x.job"), "{}");

        Assert.Equal(0, ModReview.RunSpool(spool, _ => { }));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(spool, "inbox")));
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(spool, "outbox", "7.json")))!;
        Assert.True(report["ok"]!.GetValue<bool>());
        Assert.Equal(sha, report["sha256"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("scripts/vscripts/a.nut", true)]
    [InlineData("paks/win64/a.rpak", true)]
    [InlineData("paks/win64/preload.rson", false)]
    [InlineData("resource/a.txt", false)]
    [InlineData("mod.vdf", true)]
    public void LiveEntries(string rel, bool live) => Assert.Equal(live, ModReview.IsLiveEntry(rel));

    [Theory]
    [InlineData("When: \"SERVER\"", false)]
    [InlineData("When: \"UI\"", true)]
    [InlineData("When: \"CLIENT || !SERVER\"", true)]
    [InlineData("UIX", false)]
    public void CompileListUi(string text, bool ui) => Assert.Equal(ui, ModReview.CompileListReachesUi(text));

    string PlanDigest(string rootName)
    {
        var sw = new StringWriter();
        Assert.Equal(0, ModTool.ModTool.Run(new[] { "plan", "--install-root", _root, "--root", rootName }, sw));
        foreach (var line in Lines(sw))
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("ev").GetString() == "plan")
                return doc.RootElement.GetProperty("digest").GetString()!;
        }

        throw new InvalidOperationException("no plan digest: " + sw);
    }

    static byte[] ModZip(string owner, string name, string version)
    {
        var ns = (owner + "_" + name).ToLowerInvariant();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "mod.vdf", "\"" + name + "\"\n{\n\t\"id\" \"" + owner + "." + name + "\"\n\t\"name\" \"" + name + "\"\n\t\"version\" \"" + version + "\"\n}\n");
            Add(zip, "manifest.json", "{\"name\":\"" + name + "\",\"version_number\":\"" + version + "\",\"description\":\"d\",\"dependencies\":[]}");
            Add(zip, "scripts/vscripts/scripts.rson", "When: \"SERVER\"\nScripts:\n[\n\t" + ns + "__main.nut\n]\n");
            Add(zip, "scripts/vscripts/" + ns + "__main.nut", "void function " + ns + "__Init()\n{\n\tClientCommand( \"x\" )\n}\n");
        }

        return ms.ToArray();
    }

    static void Add(ZipArchive zip, string path, string text)
    {
        var e = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var s = e.Open();
        var b = Encoding.UTF8.GetBytes(text);
        s.Write(b, 0, b.Length);
    }

    static void PlantListing(string root, params (string Owner, string Name, string Version)[] pkgs)
    {
        var arr = new JsonArray();
        foreach (var (o, n, v) in pkgs)
        {
            arr.Add(new JsonObject
            {
                ["full_name"] = o + "-" + n,
                ["owner"] = o,
                ["name"] = n,
                ["is_deprecated"] = false,
                ["has_nsfw_content"] = false,
                ["versions"] = new JsonArray(new JsonObject
                {
                    ["version_number"] = v,
                    ["download_url"] = CuratedCatalog.CanonicalDownloadUrl(o, n, v),
                    ["file_size"] = 10,
                }),
            });
        }

        var dir = Path.Combine(root, ".r5f", "cache", "thunderstore");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "listing.json"), arr.ToJsonString());
    }

    static List<string> Lines(StringWriter sw) =>
        sw.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();

    sealed class FixedHandler : HttpMessageHandler
    {
        readonly byte[] _body;

        public FixedHandler(byte[] body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
    }

    sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}
