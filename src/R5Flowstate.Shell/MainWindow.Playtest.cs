using System.Windows;
using System.Windows.Media;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

// Playtest ring: same install folder, a different CHANNEL. The reconciler
// converges the folder to whichever ring is selected, so joining and leaving
// cost only the files that differ.
public partial class MainWindow
{
    string EffectiveChannelUrl()
    {
        var env = Environment.GetEnvironmentVariable(ChannelSource.ChannelUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            return env;
        if (!string.IsNullOrWhiteSpace(_settings.ChannelUrl))
            return _settings.ChannelUrl;
        return _settings.JoinPlaytests ? ProductConstants.PlaytestChannelUrl : ProductConstants.DefaultChannelUrl;
    }

    void ApplyRingBadge()
    {
        var playtest = string.Equals(_manifest?.Channel, "playtest", StringComparison.OrdinalIgnoreCase);
        Title = playtest ? "R5Flowstate - " + Loc.Get("playtest_badge") : "R5Flowstate";
        ApplyRingSwitch();
    }

    // The LIVE | PLAYTEST switch in both headers drives the same setting as the checkbox.
    void ApplyRingSwitch()
    {
        var playtest = _settings.JoinPlaytests;
        foreach (var (live, test) in new[] { (BtnRingLive, BtnRingPlaytest), (BtnHdrRingLive, BtnHdrRingPlaytest) })
        {
            if (live is null || test is null)
                continue;
            live.Background = playtest ? Brushes.Transparent : (Brush)FindResource("PrimaryFill");
            live.Foreground = playtest ? (Brush)FindResource("TextMuted") : (Brush)FindResource("PrimaryText");
            test.Background = playtest ? (Brush)FindResource("UpdateFill") : Brushes.Transparent;
            test.Foreground = playtest ? (Brush)FindResource("UpdateText") : (Brush)FindResource("TextMuted");
        }
    }

    void OnRingLive(object sender, RoutedEventArgs e) => RequestRing(false);

    void OnRingPlaytest(object sender, RoutedEventArgs e) => RequestRing(true);

    void RequestRing(bool playtest)
    {
        if (ChkJoinPlaytests is null || _settings.JoinPlaytests == playtest)
            return;
        ChkJoinPlaytests.IsChecked = playtest;
    }

    void OnJoinPlaytestsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressArgsPersist || ChkJoinPlaytests is null)
            return;

        var wanted = ChkJoinPlaytests.IsChecked == true;
        if (wanted == _settings.JoinPlaytests)
            return;

        var root = ReadInstallPathBox();
        var (client, dedi) = ProcessSpawner.PeekRoles(root);
        if (_installBusy || _verifyBusy || client || dedi)
        {
            MessageBox.Show(this, Loc.Get("playtest_busy"), "R5Flowstate", MessageBoxButton.OK, MessageBoxImage.Information);
            ResetPlaytestBox();
            return;
        }

        if (wanted && MessageBox.Show(
                this,
                Loc.Get("playtest_confirm_on"),
                "R5Flowstate",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            ResetPlaytestBox();
            return;
        }

        _settings.JoinPlaytests = wanted;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log($"Settings save failed: {ex.Message}"); }
        Log(wanted ? "Joined the playtest ring" : "Left the playtest ring");
        _ = SwitchRingAsync(root);
    }

    // The old ring's manifest must not survive a failed fetch: an Update would
    // then converge the folder back to the ring the player just left.
    async Task SwitchRingAsync(string root)
    {
        _manifest = null;
        InvalidateHealthMemo();
        await LoadChannelAsync().ConfigureAwait(true);
        ApplyRingBadge();
        InvalidateHealthMemo();
        KickHealthRefresh(root);
        await RefreshDownloadGateAsync().ConfigureAwait(true);
        await MaybeAutoApplySmallUpdateAsync().ConfigureAwait(true);
    }

    void ResetPlaytestBox()
    {
        var was = _suppressArgsPersist;
        _suppressArgsPersist = true;
        try { ChkJoinPlaytests.IsChecked = _settings.JoinPlaytests; }
        finally { _suppressArgsPersist = was; }
        ApplyRingSwitch();
    }
}
