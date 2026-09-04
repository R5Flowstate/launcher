using System.Windows;
using System.Windows.Input;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

public partial class JoinPasswordWindow : Window
{
    private JoinPasswordWindow(string serverName)
    {
        InitializeComponent();
        TxtHeadline.Text = string.IsNullOrWhiteSpace(serverName) ? Loc.Get("join_server") : serverName;
        Loaded += (_, _) => TxtPassword.Focus();
    }

    public string Password { get; private set; } = string.Empty;

    public static bool TryAsk(Window owner, string serverName, out string password)
    {
        var w = new JoinPasswordWindow(serverName) { Owner = owner };
        var ok = w.ShowDialog() == true;
        password = ok ? w.Password : string.Empty;
        return ok;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (TxtError is not null)
            TxtError.Visibility = Visibility.Collapsed;
        if (BtnJoin is not null)
            BtnJoin.IsEnabled = TxtPassword.Password.Length > 0;
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnJoin(sender, e);
        }
    }

    private void OnJoin(object sender, RoutedEventArgs e)
    {
        var pw = TxtPassword.Password ?? string.Empty;
        if (pw.Length == 0)
            return;
        if (!LaunchArgs.IsSafeServerPassword(pw))
        {
            TxtError.Text = Loc.Get("password_illegal");
            TxtError.Visibility = Visibility.Visible;
            TxtPassword.Focus();
            return;
        }

        Password = pw;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
