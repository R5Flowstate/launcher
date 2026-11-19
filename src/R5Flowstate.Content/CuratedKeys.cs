namespace R5Flowstate.Content;

/// <summary>Public halves of the curation signing keys. The private key lives only on the master server.</summary>
public static class CuratedKeys
{
    public const string Cur1 =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAxP4HZWRoW20mqs5JTMBc\n" +
        "qEz07t8JTM+/g24gLYRzRW52LWp/6yr8oTJk/6iGS2bWmauleAONoqautveVtxuJ\n" +
        "fc/g+x0YqeidSyAoug8hTUDIS/gbIf2n+GP2Z38SUcI6X4b0x2IsvDogxAGzzkp/\n" +
        "dxOFVsSttYA0DR0nHlbq2IVetOCIN7pQGC5Cuxa/8jjXQ36dhngqkfq+60ukH2KF\n" +
        "6TfsnUvzX1YdPF86h+MFZ8BYrvFSFwhhWX2qyvJnBsOzlRE7M1tlwwrOgxW8oJRE\n" +
        "UAWWONgb4PEY5uvO4MXHFgY2L+dUvaG2a7tGMFiU3Gio686fC6281Jj7viHp74eY\n" +
        "eBYzSFhBQXf6+i0IaE9LyG7O3wBHAieDsq7ycuJgsS4jYEqjXhDgfTH8B0Jr35dH\n" +
        "YT6x2MXNdzvehEwqmYiTHBZzNYvhi1GSCy7XbuK1neimls9uBLXMC2jEcyF67Md6\n" +
        "hCUHnxAeLI+8eaYuVK6Ym5l1611FNbJc6Cf3+GeMxS4RAgMBAAE=\n" +
        "-----END PUBLIC KEY-----\n";

    /// <summary>Offline plugin key. Empty until the operator fills it; an empty slot never verifies.</summary>
    public const string Plg1 = "";

    /// <summary>Second slot, shipped before a rotation. Empty slots never verify.</summary>
    public const string Plg1Next = "";

    public static readonly IReadOnlyList<CuratedKey> PluginKeys = new[]
    {
        new CuratedKey("plg1", Plg1),
        new CuratedKey("plg1", Plg1Next),
    };
}
