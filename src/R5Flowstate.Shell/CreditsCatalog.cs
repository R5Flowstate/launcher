namespace R5Flowstate.Shell;

public sealed class CreditEntry
{
    public required string Title { get; init; }
    public required string By { get; init; }
    public required string Blurb { get; init; }
    public required string Url { get; init; }

    public string UrlHost
    {
        get
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                return Url;
            return uri.Host + uri.AbsolutePath.TrimEnd('/');
        }
    }

    public string UrlShort
    {
        get
        {
            var host = UrlHost;
            const string gh = "github.com/";
            return host.StartsWith(gh, StringComparison.OrdinalIgnoreCase)
                ? host[gh.Length..]
                : host;
        }
    }
}

/// <summary>Original public tools and authors. Links go to the upstream repo.</summary>
public static class CreditsCatalog
{
    public static IReadOnlyList<CreditEntry> All { get; } =
    [
        new()
        {
            Title = "Flowstate",
            By = "CafeFPS",
            Blurb = "S21 bridge and features. Flowstate modes. Launcher and master server.",
            Url = "https://github.com/CafeFPS",
        },
        new()
        {
            Title = "R5Valkyrie",
            By = "kralrindo",
            Blurb = "Scripts, assets management and conversion, playtesting.",
            Url = "https://github.com/kralrindo",
        },
        new()
        {
            Title = "r5sdk",
            By = "Amos",
            Blurb = "The foundation. S3 listenserver sdk. Repak.",
            Url = "https://github.com/Mauler125/r5sdk",
        },
        new()
        {
            Title = "RSX / RePak",
            By = "r-ex",
            Blurb = "Conversion suite. Rmdlconv, bspconv, rsx, repak.",
            Url = "https://github.com/r-ex",
        },
        new()
        {
            Title = "R5-AnimConv",
            By = "someoneatemylastsliceofpizza",
            Blurb = "Animation rig and sequence converter.",
            Url = "https://github.com/someoneatemylastsliceofpizza/R5-AnimConv",
        },
        new()
        {
            Title = "Assets Formats Structs",
            By = "IJARika",
            Blurb = "Templates and structs for reSource games.",
            Url = "https://github.com/IJARika/resource_model_templates",
        },
        new()
        {
            Title = "SERE",
            By = "RoyalBlue1",
            Blurb = "RUI assets editor.",
            Url = "https://github.com/RoyalBlue1/SERE",
        },
    ];
}
