using R5Flowstate.Content;
using R5Flowstate.Contracts;
using Xunit;

namespace R5Flowstate.ModToolTests;

public sealed class SessionModFilterTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "r5f-session-mods-" + Guid.NewGuid().ToString("N"));

    public SessionModFilterTests() => Directory.CreateDirectory(Path.Combine(_root, "mods"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    string VdfPath => Path.Combine(_root, "mods", ModsStore.ModListFileName);

    Dictionary<string, bool> Current() =>
        ModVdf.ReadModList(File.ReadAllText(VdfPath)).ToDictionary(r => r.Id, r => r.Enabled);

    [Fact]
    public void Restore_puts_filtered_rows_back()
    {
        var backup = ModVdf.WriteModList(new[] { ("A.Heirloom", true), ("B.Map", true) });
        var filtered = ModVdf.WriteModList(new[] { ("A.Heirloom", false), ("B.Map", true) });
        File.WriteAllText(VdfPath, filtered);

        ModsStore.RestoreFilteredModList(_root, backup, filtered);

        Assert.True(Current()["A.Heirloom"]);
        Assert.True(Current()["B.Map"]);
    }

    [Fact]
    public void Restore_keeps_changes_made_during_the_session()
    {
        var backup = ModVdf.WriteModList(new[] { ("A.Heirloom", true), ("B.Skin", true), ("C.Off", false) });
        var filtered = ModVdf.WriteModList(new[] { ("A.Heirloom", false), ("B.Skin", false), ("C.Off", false) });
        // During the session: B toggled on by hand, C toggled on, D installed.
        File.WriteAllText(VdfPath, ModVdf.WriteModList(new[]
        {
            ("A.Heirloom", false), ("B.Skin", true), ("C.Off", true), ("D.New", true),
        }));

        ModsStore.RestoreFilteredModList(_root, backup, filtered);

        var now = Current();
        Assert.True(now["A.Heirloom"]);
        Assert.True(now["B.Skin"]);
        Assert.True(now["C.Off"]);
        Assert.True(now["D.New"]);
    }

    [Fact]
    public void Restore_enables_mods_the_backup_did_not_list()
    {
        var backup = ModVdf.WriteModList(Array.Empty<(string, bool)>());
        var filtered = ModVdf.WriteModList(new[] { ("A.Heirloom", false) });
        File.WriteAllText(VdfPath, filtered);

        ModsStore.RestoreFilteredModList(_root, backup, filtered);

        Assert.True(Current()["A.Heirloom"]);
    }

    [Fact]
    public void Restore_keeps_a_mod_turned_off_before_the_join()
    {
        var backup = ModVdf.WriteModList(new[] { ("A.Heirloom", false) });
        var filtered = ModVdf.WriteModList(new[] { ("A.Heirloom", false) });
        File.WriteAllText(VdfPath, filtered);

        ModsStore.RestoreFilteredModList(_root, backup, filtered);

        Assert.False(Current()["A.Heirloom"]);
    }
}
