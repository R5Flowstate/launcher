using System.Windows;
using System.Windows.Controls;

namespace R5Flowstate.Shell;

public partial class LaunchArgsWindow : Window
{
    bool _ready;

    public event Action<string, string>? ExtrasChanged;

    public LaunchArgsWindow()
    {
        InitializeComponent();
    }

    public void Load(string clientExtra, string dediExtra, string clientApplied, string dediApplied)
    {
        _ready = false;
        TxtClientExtra.Text = clientExtra ?? string.Empty;
        TxtDediExtra.Text = dediExtra ?? string.Empty;
        SetApplied(clientApplied, dediApplied);
        _ready = true;
    }

    public void SetApplied(string clientApplied, string dediApplied)
    {
        TxtClientApplied.Text = clientApplied ?? string.Empty;
        TxtDediApplied.Text = dediApplied ?? string.Empty;
    }

    void OnExtrasChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
            return;
        ExtrasChanged?.Invoke(TxtClientExtra.Text ?? string.Empty, TxtDediExtra.Text ?? string.Empty);
    }

    void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
