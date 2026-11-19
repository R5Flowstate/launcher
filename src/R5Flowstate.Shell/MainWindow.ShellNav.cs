using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using R5Flowstate.Contracts;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

/// <summary>Header tabs, Play bar popups, Settings sections and the Advanced tab's side panel.</summary>
public partial class MainWindow
{
    // A click outside a StaysOpen=False popup closes it on mouse down, then lands on the button that opened it.
    const int PopupReopenGuardMs = 250;

    long _popupClosedAt;

    void WireShellChrome()
    {
        foreach (var check in new[] { ChkSimpleCheats, ChkSimplePassword, ChkSimpleDeveloper })
        {
            check.Checked += (_, _) => RefreshPlayPills();
            check.Unchecked += (_, _) => RefreshPlayPills();
        }

        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(TxtInstallStatus, (_, _) => RefreshInstallProgressChrome());
        DependencyPropertyDescriptor.FromProperty(RangeBase.ValueProperty, typeof(ProgressBar))
            .AddValueChanged(BarInstall, (_, _) => RefreshInstallProgressChrome());

        PanelNavTabs.SizeChanged += (_, _) => MoveNavIndicator(animate: false);
        BtnPlay.RenderTransform = _playScale;
        BarMapStrip.SizeChanged += (_, _) =>
        {
            RefreshMapStrip();
            StartMissingThumbs();
        };
        ShowCommandSegment(server: true);
        RefreshProcRows(clientAlive: false, dediAlive: false);
    }

    void RefreshInstallProgressChrome()
    {
        var idle = string.IsNullOrEmpty(TxtInstallStatus.Text) || TxtInstallStatus.Text == Loc.Get("idle");
        TxtInstallStatus.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        BarInstall.Visibility = BarInstall.Value is > 0 and < 100 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Motion: one-shot on events, nothing runs while the launcher sits idle ----

    static readonly IEasingFunction s_motionEase = CreateMotionEase();
    readonly ScaleTransform _playScale = new();
    bool _ambientHasImage;

    static IEasingFunction CreateMotionEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    static DoubleAnimation Motion(double from, double to, int ms) =>
        new(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = s_motionEase, FillBehavior = FillBehavior.Stop };

    void SetHeroImage(BitmapSource? image)
    {
        if (ImgLoadscreen is null || ReferenceEquals(ImgLoadscreen.Source, image))
            return;

        var previous = ImgLoadscreen.Source;
        ImgLoadscreen.Source = image;
        SetAmbientImage(image);
        if (image is null || previous is null)
            return;

        ImgLoadscreenPrevious.Source = previous;
        var settle = new ScaleTransform();
        ImgLoadscreen.RenderTransform = settle;
        settle.BeginAnimation(ScaleTransform.ScaleXProperty, Motion(1.05, 1, 380));
        settle.BeginAnimation(ScaleTransform.ScaleYProperty, Motion(1.05, 1, 380));
        var fade = Motion(0, 1, 240);
        fade.Completed += (_, _) => ImgLoadscreenPrevious.Source = null;
        ImgLoadscreen.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The whole page takes the art's color. A tiny copy stretched smooth is the blur, with no effect to redraw.</summary>
    // Text sits directly on this layer on every tab, so brightness is capped and hue is kept.
    static BitmapSource CappedAmbient(BitmapSource image)
    {
        const int Cap = 92;
        var scale = 32.0 / image.PixelWidth;
        var small = new FormatConvertedBitmap(
            new TransformedBitmap(image, new ScaleTransform(scale, scale)), PixelFormats.Bgr32, null, 0);
        var stride = small.PixelWidth * 4;
        var pixels = new byte[stride * small.PixelHeight];
        small.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            var grey = (r + g + b) / 3;
            r = Math.Clamp(grey + (r - grey) * 3 / 2, 0, 255);
            g = Math.Clamp(grey + (g - grey) * 3 / 2, 0, 255);
            b = Math.Clamp(grey + (b - grey) * 3 / 2, 0, 255);
            var max = Math.Max(r, Math.Max(g, b));
            if (max > Cap)
            {
                r = r * Cap / max;
                g = g * Cap / max;
                b = b * Cap / max;
            }
            pixels[i] = (byte)b;
            pixels[i + 1] = (byte)g;
            pixels[i + 2] = (byte)r;
        }
        var result = BitmapSource.Create(
            small.PixelWidth, small.PixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    void SetAmbientImage(BitmapSource? image)
    {
        if (ImgAmbient is null)
            return;
        if (image is null || image.PixelWidth == 0)
            return;

        var tiny = CappedAmbient(image);

        const double AmbientOpacity = 0.65;
        ImgAmbientPrevious.Source = ImgAmbient.Source;
        ImgAmbientPrevious.Opacity = _ambientHasImage ? AmbientOpacity : 0;
        ImgAmbient.Source = tiny;
        _ambientHasImage = true;
        ImgAmbient.Opacity = AmbientOpacity;
        ImgAmbient.BeginAnimation(OpacityProperty, Motion(0, AmbientOpacity, 420));
    }

    void AnimateHeroText()
    {
        if (PanelHeroText is null || !IsLoaded)
            return;
        var shift = new TranslateTransform();
        PanelHeroText.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, Motion(12, 0, 240));
        PanelHeroText.BeginAnimation(OpacityProperty, Motion(0, 1, 200));
    }

    void AnimateTabChange()
    {
        if (GridShellContent is null || !IsLoaded)
            return;
        ShellContentShift.BeginAnimation(TranslateTransform.YProperty, Motion(14, 0, 200));
        GridShellContent.BeginAnimation(OpacityProperty, Motion(0, 1, 170));
    }

    void MoveNavIndicator(bool animate)
    {
        if (NavIndicator is null || PanelNavTabs is null || !PanelNavTabs.IsLoaded)
            return;

        var tab = _simpleTab switch
        {
            SimpleTab.Servers => BtnTabServers,
            SimpleTab.Leaderboards => BtnTabLeaderboards,
            SimpleTab.Replays => BtnTabReplays,
            SimpleTab.Mods or SimpleTab.Plugins => BtnTabMods,
            SimpleTab.Console => BtnTabConsole,
            SimpleTab.Blog or SimpleTab.Notes => BtnTabNews,
            SimpleTab.Advanced => BtnTabAdvanced,
            SimpleTab.Settings => null,
            _ => BtnTabLocal,
        };
        if (tab is null)
        {
            NavIndicator.BeginAnimation(WidthProperty, Motion(NavIndicator.ActualWidth, 0, 140));
            NavIndicator.Width = 0;
            return;
        }

        var x = tab.TranslatePoint(new Point(0, 0), PanelNavTabs).X;
        if (animate && NavIndicator.ActualWidth > 0)
        {
            NavIndicatorShift.BeginAnimation(TranslateTransform.XProperty, Motion(NavIndicatorShift.X, x, 200));
            NavIndicator.BeginAnimation(WidthProperty, Motion(NavIndicator.ActualWidth, tab.ActualWidth, 200));
        }
        NavIndicatorShift.X = x;
        NavIndicator.Width = tab.ActualWidth;
    }

    void FlashBuildChange()
    {
        if (BuildFlash is not null && IsLoaded)
            BuildFlash.BeginAnimation(OpacityProperty, Motion(0.4, 0, 420));
    }

    void OnPlayHover(object sender, MouseEventArgs e)
    {
        var to = BtnPlay.IsMouseOver && BtnPlay.IsEnabled ? 1.04 : 1;
        var from = _playScale.ScaleX;
        var grow = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(110)) { EasingFunction = s_motionEase };
        _playScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _playScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    // ---- Tabs ----

    void OnSimpleTabNews(object sender, RoutedEventArgs e)
    {
        var notesOnly = DotNotesUnread.Visibility == Visibility.Visible
                        && DotBlogUnread.Visibility != Visibility.Visible;
        if (notesOnly)
            OnSimpleTabNotes(sender, e);
        else
            OnSimpleTabBlog(sender, e);
    }

    void OnSimpleTabAdvanced(object sender, RoutedEventArgs e)
    {
        if (TabBlockedBySetup())
            return;
        ApplySimpleTab(SimpleTab.Advanced);
    }

    void OnPluginsShowModsInstalled(object sender, RoutedEventArgs e)
    {
        OnSimpleTabMods(sender, e);
        OnModsSectionInstalled(sender, e);
    }

    void OnPluginsShowModsBrowse(object sender, RoutedEventArgs e)
    {
        OnSimpleTabMods(sender, e);
        OnModsSectionBrowse(sender, e);
    }

    // ---- Popups ----

    void TogglePopup(Popup popup)
    {
        if (Environment.TickCount64 - _popupClosedAt < PopupReopenGuardMs)
            return;
        popup.IsOpen = !popup.IsOpen;
    }

    void OnShellPopupClosed(object? sender, EventArgs e) => _popupClosedAt = Environment.TickCount64;


    void OnPillServerClick(object sender, RoutedEventArgs e) => TogglePopup(PopServer);

    void OnPillGameClick(object sender, RoutedEventArgs e) => TogglePopup(PopGame);

    // ---- Play bar ----

    void RefreshPlayPills()
    {
        if (TxtPillServer is null || _settings is null)
            return;
        TxtPillServer.Text = _settings.TickRate + " Hz";
        ChipCheats.Visibility = ChkSimpleCheats.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ChipPassword.Visibility = ChkSimplePassword.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ChipDeveloper.Visibility = ChkSimpleDeveloper.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A running session swaps the option pills for what is running; they only apply to the next start.</summary>
    void ApplyPlayBarState()
    {
        if (PanelPlayLive is null || PanelSimplePlayOptions is null)
            return;

        var live = SessionLive;
        PanelSimplePlayOptions.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        PanelPlayLive.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        if (!live)
            return;

        PopServer.IsOpen = false;
        PopGame.IsOpen = false;
        var map = !string.IsNullOrWhiteSpace(_liveMap) ? MapDisplayName(_liveMap) : _selectedMode?.SelectedMap?.DisplayName;
        var tick = _settings.TickRate + " Hz";
        TxtPlayLiveDetail.Text = string.IsNullOrWhiteSpace(map) ? tick : map + ", " + tick;
    }

    // ---- Settings ----

    void OnSettingsSectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            ShowSettingsSection(button);
    }

    void ShowSettingsSection(Button selected)
    {
        foreach (var (button, panel) in new (Button, FrameworkElement)[]
                 {
                     (BtnSetGeneral, SecGeneral),
                     (BtnSetGame, SecGame),
                     (BtnSetDownloads, SecDownloads),
                     (BtnSetAbout, SecAbout),
                 })
        {
            var on = ReferenceEquals(button, selected);
            panel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            button.Style = (Style)FindResource(on ? "RailButtonOn" : "RailButton");
        }

        ScrollSettings.ScrollToTop();
        if (ReferenceEquals(selected, BtnSetAbout))
            BindCreditsShuffled();
    }

    // ---- Advanced ----

    void OnAdvancedTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button selected)
            return;

        foreach (var (button, panel) in new (Button, FrameworkElement)[]
                 {
                     (BtnAdvTabServer, PanelAdvServer),
                     (BtnAdvTabClient, PanelAdvClient),
                     (BtnAdvTabMods, PanelAdvMods),
                     (BtnAdvTabLog, PanelAdvLog),
                 })
        {
            var on = ReferenceEquals(button, selected);
            panel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            StyleTab(button, on);
        }

        if (ReferenceEquals(selected, BtnAdvTabClient))
            ShowCommandSegment(server: false);
        else if (ReferenceEquals(selected, BtnAdvTabServer) || ReferenceEquals(selected, BtnAdvTabMods))
            ShowCommandSegment(server: true);
    }

    void OnCommandSegmentClick(object sender, RoutedEventArgs e) =>
        ShowCommandSegment(server: ReferenceEquals(sender, BtnCmdServer));

    void ShowCommandSegment(bool server)
    {
        TxtDediPreview.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
        TxtClientPreview.Visibility = server ? Visibility.Collapsed : Visibility.Visible;
        PaintSegment(BtnCmdServer, server);
        PaintSegment(BtnCmdClient, !server);
    }

    void PaintSegment(Button button, bool on)
    {
        if (on)
        {
            button.SetResourceReference(BackgroundProperty, "PrimaryFill");
            button.SetResourceReference(ForegroundProperty, "PrimaryText");
        }
        else
        {
            button.Background = Brushes.Transparent;
            button.SetResourceReference(ForegroundProperty, "TextMuted");
        }
    }

    async void OnCopyCommandClick(object sender, RoutedEventArgs e)
    {
        var preview = TxtDediPreview.Visibility == Visibility.Visible ? TxtDediPreview : TxtClientPreview;
        var command = preview.Text.Split('\n')[0].Trim();
        if (command.Length == 0)
            return;

        try
        {
            Clipboard.SetText(command);
        }
        catch (Exception ex)
        {
            Log($"Copy failed: {ex.Message}");
            return;
        }

        BtnCopyCommand.Content = Loc.Get("copied");
        await Task.Delay(1500).ConfigureAwait(true);
        BtnCopyCommand.Content = Loc.Get("copy");
    }

    void OnCopyLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TxtLog.Text);
        }
        catch (Exception ex)
        {
            Log($"Copy failed: {ex.Message}");
        }
    }

    void RefreshProcRows(bool clientAlive, bool dediAlive)
    {
        void Paint(TextBlock text, System.Windows.Shapes.Ellipse dot, string role, bool alive)
        {
            text.Text = Loc.Get(role) + "  -  " + Loc.Get(alive ? "proc_running" : "proc_stopped");
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, alive ? "Accent" : "BorderStrong");
        }

        Paint(TxtProcServer, DotProcServer, "adv_server", dediAlive);
        Paint(TxtProcClient, DotProcClient, "adv_client", clientAlive);
    }

    void OnDediPolicySegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index))
            CmbModPolicy.SelectedIndex = index;
    }

    void PaintDediPolicySegments()
    {
        PaintSegment(BtnPolicy0, _dediModPolicy == 0);
        PaintSegment(BtnPolicy1, _dediModPolicy == 1);
        PaintSegment(BtnPolicy2, _dediModPolicy == 2);
        ListDediModPolicy.IsEnabled = _dediModPolicy != 0;
        ListDediModPolicy.Opacity = _dediModPolicy != 0 ? 1 : 0.5;

        var active = _dediModRows.Count(r => r.PolicyIndex != 0);
        TxtAdvModsCount.Text = active.ToString();
        ChipAdvMods.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnDediModSearchChanged(object sender, TextChangedEventArgs e)
    {
        var needle = TxtDediModSearch.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(_dediModRows);
        view.Filter = needle.Length == 0
            ? null
            : o => o is DediModPolicyRowViewModel row
                   && row.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}
