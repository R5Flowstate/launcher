namespace R5Flowstate.ConsoleRelay;

/// <summary>
/// r5f-relay.exe — the hosted console's Windows end, run by the launcher through
/// Proton in the game's own prefix.
///
/// <para><b>What it is.</b> The Windows launcher created the console tap's two
/// named pipes in its own process: <c>R5F_CONSOLE_PIPE</c> receives the game's
/// output, <c>R5F_CONSOLE_IN</c> becomes the game's stdin. That works because the
/// launcher and the game are both Windows processes on one kernel. Here the
/// launcher is a native Linux binary, so the pipes have to be created by something
/// inside the prefix, and this exe is that something. It uses
/// <see cref="R5Flowstate.Spawn.HostedConsoleTap"/> itself — the same class, the
/// same pipe names, the same line and command rules — so the half the game sees
/// cannot drift from upstream.</para>
///
/// <para><b>The other half is a socket, deliberately.</b> The launcher cannot read
/// this process's stdout: <c>proton run</c> discards a child's streams (measured
/// three independent ways; the art host's records file exists for the same reason),
/// which is why the launcher's console pane was empty and the game opened console
/// windows of its own instead. Loopback TCP crosses the prefix boundary freely —
/// Proton's runtime makes no network namespace, so <c>127.0.0.1</c> in here is the
/// host's own loopback, the same fact EA's channel probe and bridge already rely on
/// for ports 3216/3215.</para>
///
/// <para><b>Wire format.</b> UTF-8 lines, one message per line, and the direction is
/// what says which kind it is. The relay speaks first: <c>hello&lt;TAB&gt;&lt;role
/// tag&gt;&lt;TAB&gt;&lt;out pipe path&gt;&lt;TAB&gt;&lt;in pipe path&gt;</c>, which is
/// how the launcher learns the two names to put in the game's environment — the names
/// come off the wire rather than being reconstructed, so they cannot disagree with the
/// pipes that actually exist. Every line after it is the game's console, verbatim, and
/// every line the launcher sends is a command. Nothing is tagged, so a blank console
/// line is a blank line rather than a message that has to be told apart from one the
/// launcher would ignore. A console line never contains a newline: the tap's own rule
/// already splits on '\n' and drops '\r'.</para>
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        try
        {
            return Relay.Run(args);
        }
        catch (Exception ex)
        {
            // Last words before an exit nobody sees: the launcher is waiting on a
            // socket that will now never answer, and gives up on its own deadline.
            Relay.Say($"[relay] {ex.GetType().Name}: {ex.Message}");
            return 9;
        }
    }
}
