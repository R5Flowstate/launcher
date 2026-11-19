using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using R5Flowstate.Content;
using R5Flowstate.Spawn;
using Xunit;

namespace R5Flowstate.ModToolTests;

[Collection("curated")]
public sealed class PluginTests : IDisposable
{
    static readonly RSA s_cur = RSA.Create(3072);
    static readonly RSA s_plg = RSA.Create(3072);
    static readonly RSA s_other = RSA.Create(3072);
    static readonly DateTimeOffset s_now = new(2026, 10, 22, 18, 0, 0, TimeSpan.Zero);

    readonly string _root;

    public PluginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "r5f-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "r5apex.exe"), Array.Empty<byte>());
        ModTool.ModTool.Keys = CurKeys;
        ModTool.ModTool.PluginKeys = PlgKeys;
        ModTool.ModTool.Clock = () => s_now;
        ModTool.ModTool.CuratedHandler = () => null;
        ModTool.ModTool.PluginHandler = () => null;
        PluginStatement.ResetEmptyKeyNotice();
    }

    public void Dispose()
    {
        ModTool.ModTool.Keys = CuratedCatalog.PinnedKeys;
        ModTool.ModTool.PluginKeys = CuratedKeys.PluginKeys;
        ModTool.ModTool.Clock = () => DateTimeOffset.UtcNow;
        ModTool.ModTool.CuratedHandler = () => null;
        ModTool.ModTool.PluginHandler = () => null;
        try { Directory.Delete(_root, recursive: true); } catch { /* scratch */ }
    }

    static IReadOnlyList<CuratedKey> CurKeys =>
        new[] { new CuratedKey("cur1", s_cur.ExportSubjectPublicKeyInfoPem()) };

    static IReadOnlyList<CuratedKey> PlgKeys =>
        new[] { new CuratedKey("plg1", s_plg.ExportSubjectPublicKeyInfoPem()) };

    static string Sha(char c) => new(c, 64);

    static byte[] Statement(
        string id = "chatfilter",
        string version = "1.0.0",
        string tier = "wasm",
        string realm = "server",
        bool firstParty = false,
        int api = 1,
        string[]? caps = null,
        string? artifact = null,
        long size = 4,
        string? source = null,
        string[]? builders = null,
        string[]? approved = null) =>
        PluginStatement.Format(1, id, version, tier, realm, firstParty, api,
            caps ?? new[] { "events", "script" },
            artifact ?? Sha('a'),
            size,
            source ?? Sha('b'),
            builders ?? new[] { "builder-a", "builder-b" },
            approved ?? new[] { "reviewer1" });

    static byte[] Sign(byte[] statement, RSA? key = null) =>
        (key ?? s_plg).SignData(statement, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    static string Url(string sha, string ext = "") =>
        "https://cdn.r5flowstate.org/plugins/" + sha + ext;

    static CuratedPlugin Plugin(byte[]? body = null, string id = "chatfilter", string version = "1.0.0",
        string tier = "wasm", string realm = "server", bool firstParty = false, string[]? caps = null)
    {
        body ??= Encoding.ASCII.GetBytes("wasm");
        var sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return new CuratedPlugin(id, version, tier, realm, firstParty, 1,
            caps ?? new[] { "events", "script" }, sha, body.Length, Sha('b'),
            new[] { "builder-a", "builder-b" }, new[] { "reviewer1" }, Url(sha, tier == "native" ? ".dll" : ".wasm"));
    }

    [Fact]
    public void PinnedPluginKeys_AreTwoEmptySlots()
    {
        Assert.Equal(2, CuratedKeys.PluginKeys.Count);
        Assert.All(CuratedKeys.PluginKeys, k =>
        {
            Assert.Equal("plg1", k.KeyId);
            Assert.True(string.IsNullOrEmpty(k.PublicPem));
        });
    }

    [Fact]
    public void Statement_CanonicalShape()
    {
        var text = Encoding.ASCII.GetString(Statement(size: 1234));
        Assert.Equal(
            "{\"v\":1,\"id\":\"chatfilter\",\"version\":\"1.0.0\",\"tier\":\"wasm\",\"realm\":\"server\",\"first_party\":false,\"api\":1,\"caps\":[\"events\",\"script\"],\"artifact_sha256\":\"" +
            Sha('a') + "\",\"artifact_size\":1234,\"source_sha256\":\"" + Sha('b') +
            "\",\"builders\":[\"builder-a\",\"builder-b\"],\"approved_by\":[\"reviewer1\"]}",
            text);
    }

    [Fact]
    public void Statement_Ok_BadSig_WrongKey_Tampered_EmptySlot()
    {
        var bytes = Statement();
        var sig = Sign(bytes);
        var ok = PluginStatement.Check(bytes, sig, PlgKeys, null);
        Assert.True(ok.Ok);
        Assert.Equal("chatfilter", ok.Plugin!.Id);

        var badSig = (byte[])sig.Clone();
        badSig[0] ^= 0x01;
        Assert.Equal("bad_signature", PluginStatement.Check(bytes, badSig, PlgKeys, null).Code);

        Assert.Equal("bad_signature", PluginStatement.Check(bytes, Sign(bytes, s_other), PlgKeys, null).Code);

        var tampered = (byte[])bytes.Clone();
        var marker = "\"source_sha256\":\"";
        var at = Encoding.ASCII.GetString(tampered).IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at > 0);
        tampered[at + marker.Length] = (byte)'c';
        Assert.Equal("bad_signature", PluginStatement.Check(tampered, sig, PlgKeys, null).Code);

        PluginStatement.ResetEmptyKeyNotice();
        var logs = new List<string>();
        Assert.Equal("empty_key", PluginStatement.Check(bytes, sig, CuratedKeys.PluginKeys, logs.Add).Code);
        Assert.Equal("empty_key", PluginStatement.Check(bytes, sig, CuratedKeys.PluginKeys, logs.Add).Code);
        Assert.Single(logs);
        Assert.Contains("plg1", logs[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Statement_Caps_AndNativeClientRule()
    {
        var dup = Statement(caps: new[] { "log", "log" });
        Assert.Equal("bad_statement", PluginStatement.Check(dup, Sign(dup), PlgKeys, null).Code);

        var unknown = Statement(caps: new[] { "hud" });
        Assert.Equal("bad_statement", PluginStatement.Check(unknown, Sign(unknown), PlgKeys, null).Code);

        var nine = Statement(caps: new[] { "log", "log", "log", "log", "log", "log", "log", "log", "log" });
        Assert.Equal("bad_statement", PluginStatement.Check(nine, Sign(nine), PlgKeys, null).Code);

        var all = Statement(caps: PluginStatement.CapNames);
        Assert.True(PluginStatement.Check(all, Sign(all), PlgKeys, null).Ok);

        var nativeClient = Statement(tier: "native", realm: "client", firstParty: false, size: 8);
        Assert.Equal("bad_statement", PluginStatement.Check(nativeClient, Sign(nativeClient), PlgKeys, null).Code);

        var first = Statement(tier: "native", realm: "client", firstParty: true, size: 8);
        Assert.True(PluginStatement.Check(first, Sign(first), PlgKeys, null).Ok);

        var bigWasm = Statement(size: PluginStatement.WasmMaxBytes + 1);
        Assert.Equal("bad_statement", PluginStatement.Check(bigWasm, Sign(bigWasm), PlgKeys, null).Code);
        var maxNative = Statement(tier: "native", realm: "server", size: PluginStatement.NativeMaxBytes);
        Assert.True(PluginStatement.Check(maxNative, Sign(maxNative), PlgKeys, null).Ok);
    }

    [Fact]
    public void ArtifactUrl_MustBeHttpsOnTheContentHost_EndingInTheHash()
    {
        var sha = Sha('a');
        Assert.True(PluginStatement.IsArtifactUrl(Url(sha), sha));
        Assert.True(PluginStatement.IsArtifactUrl(Url(sha, ".wasm"), sha));
        Assert.True(PluginStatement.IsArtifactUrl("https://cdn.r5flowstate.org/cas/aa/" + sha + ".dll", sha));
        Assert.False(PluginStatement.IsArtifactUrl("http://cdn.r5flowstate.org/" + sha, sha));
        Assert.False(PluginStatement.IsArtifactUrl("https://evil.example/" + sha, sha));
        Assert.False(PluginStatement.IsArtifactUrl("https://play.r5flowstate.org/" + sha, sha));
        Assert.False(PluginStatement.IsArtifactUrl("https://thunderstore.io/package/download/A/B/1.0.0/" + sha, sha));
        Assert.False(PluginStatement.IsArtifactUrl("https://203.0.113.9/" + sha, sha));
        Assert.False(PluginStatement.IsArtifactUrl(Url(sha) + "?x=1", sha));
        Assert.False(PluginStatement.IsArtifactUrl("https://cdn.r5flowstate.org/plugins/not-the-hash", sha));
        Assert.False(PluginStatement.IsArtifactUrl("https://user@cdn.r5flowstate.org/" + sha, sha));
        Assert.False(PluginStatement.IsArtifactUrl(Url(Sha('b')), sha));
    }

    [Fact]
    public void List_AcceptsVerifiedPlugin_DropsBadOnes_KeepsMods()
    {
        var good = Statement();
        var goodSha = Sha('a');
        var bad = Statement(id: "otherplug");
        var env = Envelope(Payload(new[]
        {
            Entry(good, Sign(good), Url(goodSha)),
            Entry(bad, Sign(bad, s_other), Url(goodSha, ".wasm")),
            Entry(good, Sign(good), "https://thunderstore.io/" + goodSha),
        }));
        var list = CuratedCatalog.Verify(env, CurKeys, s_now, 0, null, PlgKeys);
        Assert.Equal("Ann-A", list.Find("Ann-A")!.FullName);
        var plugin = Assert.Single(list.Plugins);
        Assert.Equal("chatfilter", plugin.Id);
        Assert.Equal(Url(goodSha), plugin.ArtifactUrl);

        PluginStatement.ResetEmptyKeyNotice();
        var logs = new List<string>();
        var closed = CuratedCatalog.Verify(env, CurKeys, s_now, 0, logs.Add, CuratedKeys.PluginKeys);
        Assert.Empty(closed.Plugins);
        Assert.NotNull(closed.Find("Ann-A"));
        Assert.Contains(logs, l => l.Contains("plg1", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_ListAndInstall_HashMismatchWritesNothing()
    {
        var body = Encoding.ASCII.GetBytes("nope");
        var stated = Sha('d');
        var statement = Statement(artifact: stated, size: body.Length);
        var env = Envelope(Payload(new[] { Entry(statement, Sign(statement), Url(stated, ".wasm")) }));
        ModTool.ModTool.CuratedHandler = () => new BodyHandler(env);
        ModTool.ModTool.PluginHandler = () => new BodyHandler(body);

        var listed = new StringWriter();
        Assert.Equal(0, ModTool.ModTool.Run(new[] { "plugins", "list", "--install-root", _root, "--refresh" }, listed));
        Assert.Contains("\"ev\":\"plugin\"", listed.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"id\":\"chatfilter\"", listed.ToString(), StringComparison.Ordinal);

        var sw = new StringWriter();
        Assert.Equal(3, ModTool.ModTool.Run(new[] { "plugins", "install", "--install-root", _root, "--refresh", "chatfilter" }, sw));
        Assert.Contains("\"code\":\"hash_mismatch\"", sw.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "platform", "plugins", "chatfilter", "1.0.0", "plugin.wasm")));
    }

    [Fact]
    public async Task Installer_HashMismatch_WritesNothing()
    {
        var body = Encoding.ASCII.GetBytes("not-the-plugin");
        var plugin = Plugin(body);
        var lie = plugin with { ArtifactSha256 = Sha('d'), ArtifactUrl = Url(Sha('d'), ".wasm") };
        var ex = await Assert.ThrowsAsync<PluginInstallException>(() =>
            PluginInstaller.InstallAsync(_root, lie, new BodyHandler(body), CancellationToken.None));
        Assert.Equal("hash_mismatch", ex.Code);
        Assert.False(File.Exists(Path.Combine(_root, "platform", "plugins", plugin.Id, plugin.Version, "plugin.wasm")));
    }

    [Fact]
    public async Task Installer_ThirdPartyNativeServerPlugin_IsRefused()
    {
        var body = Encoding.ASCII.GetBytes("native-bytes");
        var plugin = Plugin(body) with { Tier = "native", Realm = "server", FirstParty = false };
        var ex = await Assert.ThrowsAsync<PluginInstallException>(() =>
            PluginInstaller.InstallAsync(_root, plugin, new BodyHandler(body), CancellationToken.None));
        Assert.Equal("refused", ex.Code);
        Assert.False(File.Exists(Path.Combine(_root, "platform", "plugins", plugin.Id, plugin.Version, "plugin.dll")));
    }

    [Fact]
    public void EnabledJson_RoundTrip_AndAutoDisableOnRevoke()
    {
        var plugin = Plugin();
        var pin = plugin.Id + "@" + plugin.Version;
        var live = new CuratedList(1, s_now, s_now.AddDays(1),
            Array.Empty<CuratedPackage>(), Array.Empty<CuratedRevoked>(),
            new[] { plugin }, Array.Empty<CuratedPluginRevoked>());
        PluginEnablement.Write(_root, new[] { pin, plugin.Id + "@9.9.9", "nope" });
        Assert.Equal(new[] { pin }, PluginEnablement.Read(_root));
        var path = Path.Combine(_root, "platform", "plugins", "enabled.json");
        Assert.Equal("{\"v\":1,\"enabled\":[\"" + pin + "\"]}", File.ReadAllText(path));

        Assert.Empty(PluginEnablement.Reconcile(_root, live));
        Assert.Equal(new[] { pin }, PluginEnablement.Read(_root));

        var revoked = new CuratedList(2, s_now, s_now.AddDays(1),
            Array.Empty<CuratedPackage>(), Array.Empty<CuratedRevoked>(),
            new[] { plugin }, new[] { new CuratedPluginRevoked(plugin.Id, plugin.Version, "pulled") });
        Assert.Equal(new[] { pin }, PluginEnablement.Reconcile(_root, revoked));
        Assert.Empty(PluginEnablement.Read(_root));

        PluginEnablement.Write(_root, new[] { pin });
        var gone = new CuratedList(3, s_now, s_now.AddDays(1),
            Array.Empty<CuratedPackage>(), Array.Empty<CuratedRevoked>());
        Assert.Equal(new[] { pin }, PluginEnablement.Reconcile(_root, gone));
        Assert.Empty(PluginEnablement.Read(_root));
    }

    [Fact]
    public async Task AcceptedList_WritesVerbatimCuratedCopy()
    {
        var env = Envelope(Payload(Array.Empty<JsonObject>()));
        var list = await CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None, new BodyHandler(env), CurKeys, () => s_now);
        Assert.Equal(1, list.Serial);
        var copy = Path.Combine(_root, "platform", "plugins", "curated.json");
        Assert.Equal(env, File.ReadAllBytes(copy));

        var again = await CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None, new FailingHandler(), CurKeys, () => s_now);
        Assert.Equal(1, again.Serial);
        Assert.Equal(env, File.ReadAllBytes(copy));
    }

    [Fact]
    public async Task SerialWriteFailure_DoesNotAcceptTheNewList()
    {
        var first = Envelope(Payload(Array.Empty<JsonObject>(), serial: 1));
        var accepted = await CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None,
            new BodyHandler(first), CurKeys, () => s_now);
        Assert.Equal(1, accepted.Serial);
        var cache = Path.Combine(_root, ".r5f", "cache", "curated.json");
        var serialPath = Path.Combine(_root, ".r5f", "cache", "curated.serial");
        var copy = Path.Combine(_root, "platform", "plugins", "curated.json");
        var cacheBefore = File.ReadAllBytes(cache);
        var copyBefore = File.ReadAllBytes(copy);
        File.SetAttributes(serialPath, FileAttributes.ReadOnly);
        try
        {
            var second = Envelope(Payload(Array.Empty<JsonObject>(), serial: 2));
            var kept = await CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None,
                new BodyHandler(second), CurKeys, () => s_now);
            Assert.Equal(1, kept.Serial);
            Assert.Equal("1", File.ReadAllText(serialPath).Trim());
            Assert.Equal(cacheBefore, File.ReadAllBytes(cache));
            Assert.Equal(copyBefore, File.ReadAllBytes(copy));
        }
        finally
        {
            File.SetAttributes(serialPath, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task CacheWriteAfterSerialBump_RejectsOlderReplay()
    {
        var first = Envelope(Payload(Array.Empty<JsonObject>(), serial: 1));
        await CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None,
            new BodyHandler(first), CurKeys, () => s_now);
        var cache = Path.Combine(_root, ".r5f", "cache", "curated.json");
        var copy = Path.Combine(_root, "platform", "plugins", "curated.json");
        var cacheBefore = File.ReadAllBytes(cache);
        File.SetAttributes(cache, FileAttributes.ReadOnly);
        try
        {
            var second = Envelope(Payload(Array.Empty<JsonObject>(), serial: 2));
            var ex = await Assert.ThrowsAsync<CuratedListException>(() =>
                CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None,
                    new BodyHandler(second), CurKeys, () => s_now));
            Assert.Equal("catalog_unavailable", ex.Code);
            Assert.Equal("cache_write", (ex.InnerException as CuratedListException)?.Code);
            Assert.Equal(2, CuratedCatalog.ReadSerial(_root));
            Assert.Equal(cacheBefore, File.ReadAllBytes(cache));
            Assert.False(File.Exists(copy));

            var replay = await Assert.ThrowsAsync<CuratedListException>(() =>
                CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None,
                    new BodyHandler(first), CurKeys, () => s_now));
            Assert.Equal("catalog_unavailable", replay.Code);
            Assert.False(File.Exists(copy));
        }
        finally
        {
            File.SetAttributes(cache, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task GameCopyWriteFailure_DeletesStaleAndSurfaces()
    {
        var first = Envelope(Payload(Array.Empty<JsonObject>(), serial: 1));
        await CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None,
            new BodyHandler(first), CurKeys, () => s_now);
        var copy = Path.Combine(_root, "platform", "plugins", "curated.json");
        Assert.True(File.Exists(copy));
        Directory.CreateDirectory(copy + ".tmp");
        try
        {
            var second = Envelope(Payload(Array.Empty<JsonObject>(), serial: 2));
            var ex = await Assert.ThrowsAsync<CuratedListException>(() =>
                CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None,
                    new BodyHandler(second), CurKeys, () => s_now));
            Assert.Equal("plugin_list_copy", ex.Code);
            Assert.False(File.Exists(copy));
            Assert.Equal(2, CuratedCatalog.ReadSerial(_root));

            var modsOnly = await CuratedCatalog.LoadAsync(_root, refresh: true, CancellationToken.None,
                new BodyHandler(second), CurKeys, () => s_now, gameCopyOptional: true);
            Assert.Equal(2, modsOnly.Serial);
            Assert.False(File.Exists(copy));
        }
        finally
        {
            if (Directory.Exists(copy + ".tmp"))
                Directory.Delete(copy + ".tmp");
        }
    }

    [Fact]
    public void PluginRevocationReason_AcceptsPrintableAsciiIncludingPipe()
    {
        var pipe = RevokedList("broken | unsafe");
        Assert.Equal("broken | unsafe", pipe.PluginsRevoked[0].Reason);

        var edge = new string('|', 200);
        Assert.Equal(edge, RevokedList(edge).PluginsRevoked[0].Reason);

        Assert.Equal("bad_payload", RevokedCode(""));
        Assert.Equal("bad_payload", RevokedCode("bad\nreason"));
        Assert.Equal("bad_payload", RevokedCode("bad\u007freason"));
        Assert.Equal("bad_payload", RevokedCode("broken | unsafe\u00e9"));
        Assert.Equal("bad_payload", RevokedCode(new string('a', 201)));
    }

    [Fact]
    public void Statement_RegexIsWholeString_AndApprovedByIsDistinct()
    {
        Assert.False(PluginStatement.IsId("chatfilter\n"));
        Assert.False(PluginStatement.IsVersion("1.0.0\n"));
        Assert.False(PluginStatement.IsPin("chatfilter@1.0.0\n"));

        var actor = Statement(approved: new[] { "reviewer1\n" });
        Assert.Equal("bad_statement", PluginStatement.Check(actor, Sign(actor), PlgKeys, null).Code);

        var builder = Statement(builders: new[] { "builder-a\n", "builder-b" });
        Assert.Equal("bad_statement", PluginStatement.Check(builder, Sign(builder), PlgKeys, null).Code);

        var sha = Statement(artifact: Sha('a') + "\n");
        Assert.Equal("bad_statement", PluginStatement.Check(sha, Sign(sha), PlgKeys, null).Code);

        var dup = Statement(approved: new[] { "reviewer1", "reviewer1" });
        Assert.Equal("bad_statement", PluginStatement.Check(dup, Sign(dup), PlgKeys, null).Code);

        var distinct = Statement(approved: new[] { "reviewer1", "reviewer2" });
        Assert.True(PluginStatement.Check(distinct, Sign(distinct), PlgKeys, null).Ok);
    }

    [Fact]
    public void LaunchArgs_PlayerProfileNeverCarriesUnsignedPluginFlags()
    {
        var extra = "-plugins_unsigned -plugin_dev C:\\evil -plugin_dev=C:\\also -windowed";
        AssertClean(LaunchArgs.BuildClientArgs(LaunchProfile.ShippingPlayer, new ClientArgOptions { Extra = extra }));
        AssertClean(LaunchArgs.BuildDediArgs(LaunchProfile.ShippingPlayer, new DediArgOptions { Extra = extra }));

        var player = LaunchArgs.BuildClientArgs(LaunchProfile.ShippingPlayer, new ClientArgOptions
        {
            Extra = "-plugins_unsigned -windowed",
        });
        Assert.Contains("-windowed", player);
        AssertClean(player);

        var joinWithoutDev = LaunchArgs.BuildClientArgs(LaunchProfile.ShippingPlayer, new ClientArgOptions
        {
            Extra = "-plugin_dev C:\\evil",
            DropDev = true,
            ForceOnline = true,
        });
        AssertClean(joinWithoutDev);
    }

    [Fact]
    public void ModArchive_CarriesPluginSourceOnlyUnderPluginsSrc()
    {
        Assert.True(ModInstaller.IsShippableEntry("plugins/chatfilter/src/chatfilter.c"));
        Assert.True(ModInstaller.IsShippableEntry("Cafe-ChatFilter/plugins/chatfilter/src/util/words.h"));
        Assert.True(ModInstaller.IsShippableEntry("plugins/chatfilter/plugin.json"));
        Assert.True(ModInstaller.IsShippableEntry("scripts/vscripts/foo.nut"));

        Assert.False(ModInstaller.IsShippableEntry("src/chatfilter.c"));
        Assert.False(ModInstaller.IsShippableEntry("plugins/chatfilter/chatfilter.c"));
        Assert.False(ModInstaller.IsShippableEntry("plugins/Chat-Filter/src/a.c"));
        Assert.False(ModInstaller.IsShippableEntry("a/b/plugins/chatfilter/src/a.c"));
        Assert.False(ModInstaller.IsShippableEntry("plugins/chatfilter/src/plugin.wasm"));
        Assert.False(ModInstaller.IsShippableEntry("plugins/chatfilter/src/plugin.dll"));
        Assert.False(ModInstaller.IsShippableEntry("plugins/chatfilter/src/build.py"));
    }

    [Fact]
    public void LaunchArgs_DevProfilesKeepPluginDevFlag()
    {
        foreach (var profile in new[] { LaunchProfile.ShippingDev, LaunchProfile.OperatorBatParity })
        {
            var extra = "-plugin_dev C:\\plugins -windowed";
            var client = LaunchArgs.BuildClientArgs(profile, new ClientArgOptions { Extra = extra });
            var dedi = LaunchArgs.BuildDediArgs(profile, new DediArgOptions { Extra = "-plugin_dev C:\\plugins -plugins_unsigned" });
            AssertPair(client, "-plugin_dev", "C:\\plugins");
            AssertPair(dedi, "-plugin_dev", "C:\\plugins");
            Assert.Contains("-plugins_unsigned", dedi);
        }
    }

    static void AssertPair(IReadOnlyList<string> args, string key, string value)
    {
        var at = args.ToList().IndexOf(key);
        Assert.True(at >= 0 && at + 1 < args.Count, key + " missing");
        Assert.Equal(value, args[at + 1]);
    }

    static void AssertClean(IReadOnlyList<string> args)
    {
        Assert.DoesNotContain(args, t =>
            t.Contains("plugin", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("evil", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("also", StringComparison.OrdinalIgnoreCase));
    }

    static JsonObject Entry(byte[] statement, byte[] sig, string url) => new()
    {
        ["key_id"] = "plg1",
        ["statement"] = Convert.ToBase64String(statement),
        ["sig"] = Convert.ToBase64String(sig),
        ["artifact_url"] = url,
    };

    static byte[] Payload(JsonObject[] plugins, long serial = 1, JsonArray? pluginsRevoked = null)
    {
        var pkg = new JsonObject
        {
            ["full_name"] = "Ann-A",
            ["owner"] = "Ann",
            ["name"] = "A",
            ["official"] = false,
            ["description"] = "desc",
            ["versions"] = new JsonArray(new JsonObject
            {
                ["version"] = "1.0.0",
                ["sha256"] = Sha('a'),
                ["size"] = 5,
                ["download_url"] = CuratedCatalog.CanonicalDownloadUrl("Ann", "A", "1.0.0"),
                ["approved_at"] = "2026-10-22T17:00:00Z",
                ["restart"] = false,
                ["deps"] = new JsonArray(),
            }),
        };
        var root = new JsonObject
        {
            ["v"] = 1,
            ["serial"] = serial,
            ["issued_at"] = "2026-10-22T12:00:00Z",
            ["expires_at"] = "2026-10-23T12:00:00Z",
            ["community"] = "r5flowstate",
            ["packages"] = new JsonArray(pkg),
            ["revoked"] = new JsonArray(),
            ["plugins"] = new JsonArray(plugins.Select(p => (JsonNode)p).ToArray()),
        };
        if (pluginsRevoked is not null)
            root["plugins_revoked"] = pluginsRevoked;
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    static CuratedList RevokedList(string reason) =>
        CuratedCatalog.Verify(Envelope(Payload(Array.Empty<JsonObject>(), pluginsRevoked: new JsonArray(new JsonObject
        {
            ["id"] = "chatfilter",
            ["version"] = "1.0.0",
            ["reason"] = reason,
        }))), CurKeys, s_now, 0);

    static string RevokedCode(string reason)
    {
        var ex = Assert.Throws<CuratedListException>(() => RevokedList(reason));
        return ex.Code;
    }

    static byte[] Envelope(byte[] payload)
    {
        var sig = s_cur.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Encoding.UTF8.GetBytes(new JsonObject
        {
            ["v"] = 1,
            ["key_id"] = "cur1",
            ["payload"] = Convert.ToBase64String(payload),
            ["sig"] = Convert.ToBase64String(sig),
        }.ToJsonString());
    }

    sealed class BodyHandler : HttpMessageHandler
    {
        readonly byte[] _body;
        public BodyHandler(byte[] body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
    }

    sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}
