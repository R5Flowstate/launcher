namespace R5Flowstate.Linux.Core;

/// <summary>What a line from the client says about platform identity — whether the
/// game got an account to prove.</summary>
public enum PlatformIdentityVerdict
{
    /// <summary>Nothing in this line is about identity.</summary>
    None,

    /// <summary>The game used a real Nucleus identity for its handshake. The
    /// strongest thing this client ever prints.</summary>
    HandshakeUsedIdentity,

    /// <summary>A platform token arrived.</summary>
    Ready,

    /// <summary>Identity is being waited for. Not a failure — yet.</summary>
    Waiting,

    /// <summary>No token, or a connect dropped because of it.</summary>
    Failed,

    /// <summary>This client never expects a token (offline lanes), so its absence
    /// is not a failure.</summary>
    NoTokenExpected,

    /// <summary>A loopback connect went ahead without a token. Informational: the
    /// game reached the server by itself, which is not the same as proving who it
    /// is.</summary>
    LoopbackNoToken,

    /// <summary>The game's own install check could not be satisfied — it cannot see
    /// an EA App. Fatal, and it is the discovery problem the split can hit.</summary>
    InstallCheckFatal,

    /// <summary>The offline guard's handshake patch did not take. Informational.</summary>
    HandshakeGuardInactive,

    /// <summary>The guard blocked the game from launching the EA client. Healthy
    /// when the EA App is already up — the guard's whole job is to stop a second
    /// copy.</summary>
    LaunchBlocked,
}

/// <summary>
/// The game's own words, as rules.
///
/// The launcher cannot prove identity itself: the LSX handshake is an AES
/// challenge-response against <c>recipient: EbisuSDK</c>/<c>EALS</c>, and the
/// launcher holds no key material (see <see cref="EaChannelProbe"/>, which is why
/// a connect is described as necessary and not sufficient). What it can do is read
/// what the client prints about itself — these literals are in the installed lane's
/// <c>client.dll</c> — and turn them into one verdict a player can act on.
///
/// <para><b>Absence is never a verdict.</b> Whether these <c>Msg</c> lines reach
/// stdout without <c>-devsdk</c> is unverified, so a client that prints none of
/// them is <see cref="PlatformIdentityVerdict.None"/> — "no news", never "broken".
/// Only the literals below can move the verdict.</para>
///
/// Pure and fixture-testable on purpose: one line in, one verdict out, no state.
/// </summary>
public static class PlatformIdentityLines
{
    /// <summary>The verdict this line carries, or <see cref="PlatformIdentityVerdict.None"/>.</summary>
    public static PlatformIdentityVerdict Classify(string? line)
    {
        var text = line ?? "";
        if (text.Length == 0)
            return PlatformIdentityVerdict.None;

        // The strongest first: a handshake that used a real identity is the point
        // of the whole exercise, and it must not be shadowed by a later "waiting"
        // line from the same pass.
        if (text.Contains("HANDSHAKE: using nucleusId", StringComparison.Ordinal))
            return PlatformIdentityVerdict.HandshakeUsedIdentity;

        if (text.Contains("[EbisuSDK] platform token arrived", StringComparison.Ordinal))
            return PlatformIdentityVerdict.Ready;
        if (text.Contains("platform identity enabled; signed token will be requested", StringComparison.Ordinal))
            return PlatformIdentityVerdict.Waiting;

        if (text.Contains("platform token never arrived", StringComparison.Ordinal))
            return PlatformIdentityVerdict.Failed;
        if (text.Contains("Origin identity never arrived", StringComparison.Ordinal))
            return PlatformIdentityVerdict.Failed;

        if (text.Contains("no platform token expected on this client", StringComparison.Ordinal))
            return PlatformIdentityVerdict.NoTokenExpected;

        if (text.Contains("HANDSHAKE hold: Origin identity not ready", StringComparison.Ordinal))
            return PlatformIdentityVerdict.Waiting;
        if (text.Contains("[JOIN-AUTH] holding connect to", StringComparison.Ordinal))
            return PlatformIdentityVerdict.Waiting;

        if (text.Contains("loopback connect proceeding without token", StringComparison.Ordinal))
            return PlatformIdentityVerdict.LoopbackNoToken;

        // The install check's own failure. Matched on the pair of fragments rather
        // than one literal: the guard's message names a patch letter and the check
        // it could not satisfy, and both parts have to be there for it to mean
        // "the game cannot see an EA install".
        if (text.Contains("[OFFLINE-GUARD]", StringComparison.Ordinal)
            && text.Contains("install check still fatal", StringComparison.Ordinal))
            return PlatformIdentityVerdict.InstallCheckFatal;

        if (text.Contains("[OFFLINE-GUARD]", StringComparison.Ordinal)
            && text.Contains("running EA App still handshakes", StringComparison.Ordinal))
            return PlatformIdentityVerdict.HandshakeGuardInactive;

        if (text.Contains("[OFFLINE-GUARD]", StringComparison.Ordinal)
            && text.Contains("platform client launch BLOCKED", StringComparison.Ordinal))
            return PlatformIdentityVerdict.LaunchBlocked;

        return PlatformIdentityVerdict.None;
    }

    /// <summary>True when the verdict is something the player has to act on.</summary>
    public static bool IsFailure(PlatformIdentityVerdict verdict)
        => verdict is PlatformIdentityVerdict.Failed or PlatformIdentityVerdict.InstallCheckFatal;

    /// <summary>True when the verdict is evidence identity worked.</summary>
    public static bool IsSuccess(PlatformIdentityVerdict verdict)
        => verdict is PlatformIdentityVerdict.HandshakeUsedIdentity or PlatformIdentityVerdict.Ready;

    /// <summary>The sentence the card shows. Every failure says what to do about
    /// it — and until the split runtime was removed, the thing to do was a settings
    /// switch ("switch EA Runtime to Proton", "press Bridge EA install"). Both of
    /// those are gone, so the two failure sentences name the steps that still exist
    /// rather than a switch that no longer does: with one prefix, an EA App that is
    /// installed and running is the whole of it.</summary>
    public static string Describe(PlatformIdentityVerdict verdict) => verdict switch
    {
        PlatformIdentityVerdict.HandshakeUsedIdentity =>
            "identity: the game handshook with a real Nucleus account — EA is vouching for you",
        PlatformIdentityVerdict.Ready =>
            "identity: a platform token arrived from the EA App",
        PlatformIdentityVerdict.Waiting =>
            "identity: still waiting for the EA App (it must be running and signed in)",
        PlatformIdentityVerdict.Failed =>
            "identity: no token arrived — the game cannot prove this account to the master "
            + "server. Keep the EA App open and signed in; the game and the EA App share one "
            + "prefix, so EA's own process is in the game's process list already, and nothing "
            + "here has to be switched over",
        PlatformIdentityVerdict.NoTokenExpected =>
            "identity: this client expects no token (offline lane) — nothing is wrong",
        PlatformIdentityVerdict.LoopbackNoToken =>
            "identity: the game connected over loopback without a token — it reached the "
            + "server, but not as a signed-in account",
        PlatformIdentityVerdict.InstallCheckFatal =>
            "identity: the game's EA install check failed, so it cannot see the EA App at all. "
            + "Install the EA App into the game's prefix from Settings and leave it installed "
            + "— the check reads the very prefix the game runs in",
        PlatformIdentityVerdict.HandshakeGuardInactive =>
            "identity: the offline guard's handshake patch is not active — informational",
        PlatformIdentityVerdict.LaunchBlocked =>
            "identity: the game was blocked from launching its own EA client — healthy when "
            + "the EA App is already running",
        _ => "",
    };
}
