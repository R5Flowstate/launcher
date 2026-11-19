using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

// Server tick rate: one setting for launcher-started servers (+bridge_netmode /
// +bridge_tickrate) and for matches the client starts from its own lobby
// (+cl_local_match_tickrate). A running server keeps its rate until relaunched.
public partial class MainWindow
{
    void OnTickRateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }
            || !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hz))
            return;
        hz = TickRates.Normalize(hz, LauncherSettings.DefaultTickRate);
        if (_settings.TickRate == hz)
            return;
        _settings.TickRate = hz;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
        ApplyTickRateSegments();
        RefreshArgPreviews();
    }

    void ApplyTickRateSegments()
    {
        var on = (Brush)FindResource("PrimaryFill");
        var onText = (Brush)FindResource("PrimaryText");
        var offText = (Brush)FindResource("TextMuted");
        foreach (var btn in new[] { BtnTick20, BtnTick60, BtnTick120, BtnAdvTick20, BtnAdvTick60, BtnAdvTick120 })
        {
            if (btn is null)
                continue;
            var selected = btn.Tag is string tag && tag == _settings.TickRate.ToString(CultureInfo.InvariantCulture);
            btn.Background = selected ? on : Brushes.Transparent;
            btn.Foreground = selected ? onText : offText;
        }
        RefreshPlayPills();
    }
}
