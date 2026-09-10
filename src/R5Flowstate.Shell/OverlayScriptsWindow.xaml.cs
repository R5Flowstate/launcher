using System.Windows;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

public partial class OverlayScriptsWindow : Window
{
    OverlayScriptsWindow(OverlayEditReport report)
    {
        InitializeComponent();
        TxtDetail.Text = Loc.Format("overlay_detail", report.Changed.Count, report.Extra.Count);
        var samples = report.Changed.Concat(report.Extra).Take(8).ToList();
        var extra = report.Total > samples.Count
            ? "\n" + Loc.Format("overlay_more", report.Total - samples.Count)
            : "";
        TxtSamples.Text = string.Join("\n", samples) + extra;
    }

    public static OverlayExtractPolicy Ask(Window owner, OverlayEditReport report)
    {
        var w = new OverlayScriptsWindow(report) { Owner = owner };
        var ok = w.ShowDialog() == true;
        return ok ? OverlayExtractPolicy.WriteOfficial : OverlayExtractPolicy.KeepEdits;
    }

    void OnRestore(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    void OnLeave(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
