using Velopack;

namespace R5Flowstate.HostGui;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Exits on its own for --veloapp-* hooks; a no-op when not installed by Setup.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
