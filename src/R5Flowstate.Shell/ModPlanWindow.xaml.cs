using System.Windows;

namespace R5Flowstate.Shell;

public sealed class ModPlanSpec
{
    public string Kicker { get; init; } = string.Empty;
    public string Headline { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public IReadOnlyList<ModPlanRow> Rows { get; init; } = Array.Empty<ModPlanRow>();
    public string PrimaryText { get; init; } = string.Empty;
    public bool Danger { get; init; }

    /// <summary>Download bytes; negative hides the totals row.</summary>
    public long DownloadBytes { get; init; } = -1;

    public long NeededBytes { get; init; }

    /// <summary>Free bytes on the install drive; negative when unknown.</summary>
    public long FreeBytes { get; init; } = -1;
}

public partial class ModPlanWindow : Window
{
    private ModPlanWindow(ModPlanSpec spec)
    {
        InitializeComponent();
        if (spec.Kicker.Length > 0)
        {
            TxtKicker.Text = spec.Kicker.ToUpperInvariant();
            TxtKicker.Visibility = Visibility.Visible;
        }

        TxtHeadline.Text = spec.Headline;
        TxtBody.Text = spec.Body;
        TxtBody.Visibility = spec.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ListRows.ItemsSource = spec.Rows;
        BtnPrimary.Content = spec.PrimaryText;
        if (spec.Danger)
        {
            BtnPrimary.Background = (System.Windows.Media.Brush)FindResource("DangerFill");
            BtnPrimary.Foreground = System.Windows.Media.Brushes.White;
        }

        if (spec.DownloadBytes >= 0)
        {
            PanelTotals.Visibility = Visibility.Visible;
            TxtTotalDownload.Text = MainWindow.FormatBytes(spec.DownloadBytes);
            TxtTotalSpace.Text = Loc.Format("mods_about", MainWindow.FormatBytes(spec.NeededBytes));
            TxtTotalFree.Text = spec.FreeBytes >= 0 ? MainWindow.FormatBytes(spec.FreeBytes) : Loc.Get("n_a");
            if (spec.FreeBytes >= 0 && spec.FreeBytes < spec.NeededBytes)
            {
                TxtTotalFree.Foreground = (System.Windows.Media.Brush)FindResource("DangerFg");
                TxtWarning.Text = Loc.Format(
                    "mods_space_short",
                    MainWindow.FormatBytes(spec.NeededBytes - spec.FreeBytes));
                TxtWarning.Visibility = Visibility.Visible;
                BtnPrimary.IsEnabled = false;
            }
            else
            {
                TxtTotalFree.Foreground = (System.Windows.Media.Brush)FindResource("AccentBright");
            }
        }
    }

    public static bool Ask(Window owner, ModPlanSpec spec)
    {
        var w = new ModPlanWindow(spec) { Owner = owner };
        return w.ShowDialog() == true;
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
