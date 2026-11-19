using System.Windows;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

/// <summary>Client launch line. The server's line lives in Server Settings.</summary>
public partial class LaunchArgsWindow : Window
{
    /// <summary>(extra args, unticked keys)</summary>
    public event Action<string, string>? ArgsChanged;

    public LaunchArgsWindow()
    {
        InitializeComponent();
        ClientArgs.Changed += () => ArgsChanged?.Invoke(ClientArgs.Extra, ClientArgs.DisabledKeys);
    }

    public void Load(string extra, string disabledKeys, IReadOnlyList<string> generated, string applied) =>
        ClientArgs.Load(extra, disabledKeys, generated, LaunchArgs.ClientCoreKeys, applied);

    public void SetApplied(string applied) => ClientArgs.SetApplied(applied);

    void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
