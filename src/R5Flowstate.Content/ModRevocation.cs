using R5Flowstate.Contracts;

namespace R5Flowstate.Content;

/// <summary>A version the reviewed list revoked must not load: its installed copy is turned off in mods.vdf.</summary>
public static class ModRevocation
{
    /// <summary>Pass the current list, or <see cref="CuratedCatalog.LoadLastKnown"/> when none can be checked.</summary>
    public static IReadOnlyList<string> Reconcile(string installRoot, CuratedList? list)
    {
        if (list is null || list.Revoked.Count == 0)
            return Array.Empty<string>();

        var off = new List<string>();
        foreach (var mod in ModsStore.Discover(installRoot))
        {
            if (!mod.Enabled || !ModId.IsValid(mod.Id) || !VerifiedInstalls.IsRevoked(mod, list))
                continue;
            ModsStore.SetEnabled(installRoot, mod.Id, false);
            off.Add(mod.Id);
        }

        return off;
    }

    /// <summary>The installed mods a server or a join may still use.</summary>
    public static IReadOnlyList<InstalledMod> Usable(IReadOnlyList<InstalledMod> installed, CuratedList? list)
    {
        if (list is null || list.Revoked.Count == 0)
            return installed;
        return installed.Where(m => !VerifiedInstalls.IsRevoked(m, list)).ToList();
    }
}
