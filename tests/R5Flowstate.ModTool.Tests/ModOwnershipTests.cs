using R5Flowstate.Contracts;
using Xunit;

namespace R5Flowstate.ModToolTests;

public class ModOwnershipTests
{
    const string Vdf = """
        "mod"
        {
        	"id"	"Skep.WeaponPack"
        	"DatatableAppends"
        	{
        		"survival_weapon_mods"	"1"
        		"weapon_attachment_matrix"	"1"
        	}
        }
        """;

    [Fact]
    public void Appends_OwnOnlyTheirDeclaredFiles()
    {
        var doc = ModVdf.Parse(Vdf);
        var appends = ModOwnership.ReadDatatableAppends(doc, out var error);
        Assert.NotNull(appends);
        Assert.Equal(string.Empty, error);

        var none = Array.Empty<string>();
        Assert.True(ModOwnership.OwnsPath("skep_weaponpack", none, none, "datatable_append/survival_weapon_mods.csv", null, appends));
        Assert.False(ModOwnership.OwnsPath("skep_weaponpack", none, none, "datatable_append/survival_loot.csv", null, appends));
        Assert.False(ModOwnership.OwnsPath("skep_weaponpack", none, none, "datatable/survival_weapon_mods.csv", null, appends));
    }

    [Fact]
    public void Appends_RefuseATableThatIsAlsoReplaced()
    {
        var doc = ModVdf.Parse("""
            "mod"
            {
            	"id"	"Skep.WeaponPack"
            	"DatatableOverrides"	{ "survival_loot" "1" }
            	"DatatableAppends"	{ "survival_loot" "1" }
            }
            """);
        Assert.Null(ModOwnership.ReadDatatableAppends(doc, out var error));
        Assert.Contains("both replaced and appended", error, StringComparison.Ordinal);
    }
}
