using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using R5Flowstate.Content;
using R5Flowstate.Contracts;
using R5Flowstate.Linux.Core;
// The ported master-server type. This class also has a *property* called
// DownloadStatus (the transfer line), which would shadow it, so the type gets a
// short alias here rather than either one being renamed.
using Lanes = R5Flowstate.Shell.Linux.DownloadStatus;

namespace R5Flowstate.Shell.Linux.ViewModels;

/// <summary>
/// Game files: channel manifest, install, update, verify, repair.
///
/// None of this needed porting — <c>R5Flowstate.Content</c> is net8.0 and
/// platform-neutral, and it ships byte-identical to upstream. What was missing was
/// the wiring upstream's <c>MainWindow.xaml.cs</c> has around it: load the channel,
/// assess the tree, run the reconciler with progress, report the health line.
///
/// The install path here is the content-addressed store (CONTENT_MANIFEST →
/// cas/&lt;shard&gt;/&lt;sha256&gt;), not the 7z volume path: it needs no external 7-Zip
/// binary and is the path the live channel tips point at.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _installing;
    [ObservableProperty] private bool _installPaused;
    [ObservableProperty] private double _installFraction;
    [ObservableProperty] private string _channelStatus = "";

    // ---- the simple-mode progress card's slots -------------------------------
    // Upstream's TxtSimpleProgressPhase / TxtSimpleProgressFile /
    // TxtSimpleProgressBytes / TxtSimpleProgressStep / TxtSimpleProgressRate.
    // They are separate properties rather than one composed sentence because the
    // card lays them out in four different rows — the composed sentence is
    // InstallStatus, which the advanced card and the status bar keep using.
    // Without them the install tab had a bar and nothing else: the port had
    // brought over upstream's simple *form* and not its progress card, so a
    // download in simple mode moved no bar on the tab the player was looking at.

    /// <summary>The phase, already localized ("Downloading", "Checking").</summary>
    [ObservableProperty] private string _installPhase = "";

    /// <summary>The file being moved, or "412 of 900 files" while a scan counts
    /// names rather than bytes (upstream's rule, kept).</summary>
    [ObservableProperty] private string _installFileLine = "";

    /// <summary>Bytes done out of bytes planned — the card's top-right figure.</summary>
    [ObservableProperty] private string _installBytes = "";

    /// <summary>"part 2 of 5 · track". Empty when the engine reports no steps.</summary>
    [ObservableProperty] private string _installStep = "";

    /// <summary>The smoothed rate, empty until there is one worth showing.</summary>
    [ObservableProperty] private string _installRateText = "";

    /// <summary>What is in flight right now, for the card's list. Rebuilt per
    /// tick: the engine hands over an immutable snapshot, so there is nothing
    /// incremental to preserve (upstream's ListActiveTransfers does the same).</summary>
    public ObservableCollection<TransferRowVM> InstallActive { get; } = new();

    ChannelManifest? _channel;
    CancellationTokenSource? _installCts;
    InstallRunControl? _installRun;
    InstallHealthReport? _healthMemo;
    string _healthMemoRoot = "";
    DateTime _healthMemoUtc;
    bool _contentBusy;

    /// <summary>An install/verify/repair is running. Windows' ConsolePickersOpen
    /// keeps the mode/map pickers away while the caption is one of the install
    /// actions, because there is no level yet to steer; the Linux port has one
    /// flag for that state rather than the play-kind enum, so it is the flag
    /// that gates them.</summary>
    public bool InstallInFlight => _contentBusy;

    // ---- the setup card's own words -----------------------------------------
    // Windows re-writes these three in PaintInstallControls, which sets .Text
    // straight onto TextBlocks that XAML had bound to {loc:T …} — so on that side
    // the first paint kills the language binding. Here they are computed instead,
    // and the two events that can change them (the busy flag, the pause flag) plus
    // the language switch are the only things that have to announce them.

    /// <summary>The setup card's headline: "Installing the game" while a job runs,
    /// "Install the game" otherwise.</summary>
    public string SimpleSetupTitle =>
        Loc.Get(Installing ? "installing_the_game" : "install_the_game");

    /// <summary>Its kicker over the headline, above it.</summary>
    public string SimpleSetupKicker => Loc.Get(Installing ? "hang_tight" : "get_started");

    /// <summary>The install card's pause button, which is its resume button once
    /// the job is held.</summary>
    public string InstallPauseLabel => Loc.Get(InstallPaused ? "resume" : "pause");

    partial void OnInstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(SimpleSetupTitle));
        OnPropertyChanged(nameof(SimpleSetupKicker));
    }

    partial void OnInstallPausedChanged(bool value) =>
        OnPropertyChanged(nameof(InstallPauseLabel));

    /// <summary>A language change is the one event that moves a computed localized
    /// string without moving anything it reads, so it says so itself (the
    /// {loc:T} bindings look after the rest of the shell).</summary>
    internal void NotifyLocalizedInstallStrings()
    {
        OnPropertyChanged(nameof(SimpleSetupTitle));
        OnPropertyChanged(nameof(SimpleSetupKicker));
        OnPropertyChanged(nameof(InstallPauseLabel));
    }

    /// <summary>The card's own slots, back to nothing. Called as a job starts, so
    /// a second run cannot open on the first one's phase and file.</summary>
    internal void ClearInstallCard()
    {
        InstallPhase = "";
        InstallFileLine = "";
        InstallBytes = "";
        InstallStep = "";
        InstallRateText = "";
        InstallFraction = 0;
        InstallActive.Clear();
    }

    /// <summary>The card's in-flight list, rebuilt on the UI thread from the
    /// engine's snapshot. Names are reduced to the file's own name — the full
    /// content-store path is a shard and two hashes, which is not a thing to read
    /// while waiting (upstream trims at the last slash the same way).</summary>
    internal void SetInstallTransfers(IReadOnlyList<ActiveTransfer>? active)
    {
        InstallActive.Clear();
        if (active is null) return;
        foreach (var t in active)
        {
            var path = t.Path ?? "";
            var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            var name = cut >= 0 && cut + 1 < path.Length ? path[(cut + 1)..] : path;
            InstallActive.Add(new TransferRowVM
            {
                Name = name,
                Detail = t.Total > 0
                    ? FormatBytes(t.Done) + " / " + FormatBytes(t.Total)
                    : FormatBytes(t.Done),
                Percent = t.Percent,
            });
        }
    }

    /// <summary>The channel manifest is cached next to settings so a later Assess
    /// works offline, the same way upstream caches it under the install root.</summary>
    static string ChannelCacheDir => Path.Combine(LinuxSettings.ConfigDir, "channel");

    string EffectiveChannelUrl =>
        string.IsNullOrWhiteSpace(_s.ChannelUrl) ? LinuxSettings.DefaultChannelUrl : _s.ChannelUrl.Trim();

    /// <summary>Applies the Settings → Downloads limits to the engine's statics.
    /// Upstream does the same when the boxes change (MainWindow.Console.cs).</summary>
    void ApplyTransferLimits()
    {
        FileSystemFetcher.GlobalMaxBytesPerSecond = DownloadLimit > 0 ? DownloadLimit * 1_000_000L : 0;
        ContentExecutor.GlobalConcurrency = _s.DownloadConcurrency;
    }

    /// <summary>Loads CHANNEL_MANIFEST.json (once), keeping the previous manifest
    /// on failure so a network blip does not blank the tab.</summary>
    public async Task<ChannelManifest?> LoadChannelAsync(bool force = false)
    {
        if (_channel is not null && !force)
            return _channel;

        ApplyTransferLimits();
        using var fetcher = new FileSystemFetcher();
        try
        {
            ChannelStatus = "channel: fetching…";
            var channel = await ChannelSource.LoadAsync(
                EffectiveChannelUrl, fetcher, ChannelCacheDir, CancellationToken.None)
                .ConfigureAwait(true);

            _channel = channel;
            ChannelStatus = $"channel: {channel.Channel} · gate {channel.EffectiveGateName}";
            AppendLog($"Channel {channel.Channel} (gate {channel.EffectiveGateName}) from {EffectiveChannelUrl}");
            foreach (var (name, tip) in Tips(channel))
                AppendLog($"  {name}: {tip.CatalogVersion} · {Describe(tip)}");
            // The channel says what exists; the master says which lanes are
            // open. Windows fetches the two together at startup (channel, then
            // gate), so a closed lane is known before the first button press.
            await RefreshDownloadGateAsync().ConfigureAwait(true);
            await RefreshInstallSizeAsync().ConfigureAwait(true);
            // Notes live next to the channel manifest, so the channel is what
            // tells us where to look. Fetched in the background; the tab keeps
            // showing the cached copy until it lands.
            _ = RefreshNotesAsync();
            return _channel;
        }
        catch (Exception ex)
        {
            ChannelStatus = "channel: " + ex.Message;
            AppendLog("Channel load failed: " + ex.Message);
            return _channel;
        }
    }

    static IEnumerable<(string Name, ChannelTrackTip Tip)> Tips(ChannelManifest channel)
    {
        if (channel.Client is not null) yield return ("client", channel.Client);
        if (channel.Server is not null) yield return ("server", channel.Server);
        if (channel.Platform is not null) yield return ("platform", channel.Platform);
        if (channel.Hd is not null) yield return ("hd", channel.Hd);
    }

    // ------------------------------------------------- master-server lanes
    // GET /launcher/status. The channel publishes what exists; this says which
    // lanes the operator is serving right now, which is what decides whether an
    // install may fetch a track and whether a behind install blocks PLAY.

    // The type lives in R5Flowstate.Shell.Linux; this class has a *property*
    // called DownloadStatus (the transfer line), so the two are told apart by
    // this alias rather than by renaming either one.
    Lanes _downloadGate = Lanes.AllowAll;
    DateTime _downloadGateUtc;

    /// <summary>A local channel (file://, or a plain path) belongs to whoever is
    /// running it, so the public master's lane switches do not apply to it —
    /// Windows parity: ChannelIsLocal.</summary>
    public bool ChannelIsLocal => ChannelSource.LooksLocal(EffectiveChannelUrl);

    /// <summary>
    /// Fetches the lane switches. Fails open in both directions, like Windows:
    /// an unreachable master leaves INSTALL able to try the CDN and never blocks
    /// PLAY — a launcher must not refuse to start a game because a web host is
    /// down. A local channel is always AllowAll.
    /// </summary>
    public async Task<Lanes> RefreshDownloadGateAsync()
    {
        if (ChannelIsLocal)
        {
            _downloadGate = Lanes.AllowAll;
            _downloadGateUtc = DateTime.UtcNow;
            return _downloadGate;
        }

        try
        {
            _downloadGate = await MasterServerClient
                .GetDownloadStatusAsync(MasterServerUrl)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog("Download gate fetch failed (play allowed): " + ex.Message);
            _downloadGate = Lanes.Unreachable;
        }

        _downloadGateUtc = DateTime.UtcNow;
        AppendLog($"Download gate: reachable={_downloadGate.Reachable} setup={_downloadGate.Setup} " +
                  $"content={_downloadGate.Content} platform={_downloadGate.Platform} dedi={_downloadGate.Dedi}");
        OnPropertyChanged(nameof(DediPackageAvailable));
        return _downloadGate;
    }

    /// <summary>The gate as last fetched, without a round trip.</summary>
    public Lanes CachedDownloadGate => ChannelIsLocal ? Lanes.AllowAll : _downloadGate;

    /// <summary>
    /// The standalone server package is linked only when the master says it is
    /// published. Old masters carry no dedi field and default to off, so the link
    /// is absent rather than 404 (Windows parity: ApplyDediPackageVisibility).
    /// </summary>
    public bool DediPackageAvailable => CachedDownloadGate.Dedi;

    /// <summary>
    /// True only when an open lane is the reason the install is behind: content
    /// or platform is being served, and the track that is behind is one of those.
    /// An unreachable master returns false, so PLAY keeps working.
    /// </summary>
    public static bool LaneBlocksPlay(
        InstallHealthReport health, Lanes gate, bool requireClient, bool requireServer)
    {
        if (!gate.Reachable)
            return false;
        if (gate.Content && requireClient && health.Client?.Status == InstallHealthStatus.UpdateAvailable)
            return true;
        if (gate.Content && requireServer && health.Server?.Status == InstallHealthStatus.UpdateAvailable)
            return true;
        if (gate.Platform && health.Platform?.Status == InstallHealthStatus.UpdateAvailable)
            return true;
        return false;
    }

    /// <summary>
    /// What the download will cost, from the planner upstream uses: the full size
    /// of the tracks this install needs, and how much of that is still to fetch.
    /// Returns false when there is no channel or the numbers cannot be produced.
    /// </summary>
    public static bool TryMeasureInstallDisk(
        ChannelManifest? channel, string installPath, out long planned, out long need, out long free)
    {
        planned = 0;
        need = 0;
        free = -1;
        if (channel is null || string.IsNullOrWhiteSpace(installPath)) return false;

        try
        {
            var plan = InstallPlanner.Build(channel, InstallMode.Full, installPath);
            InstallPlanner.FilterDownloadLanes(plan, true, true);
            InstallPlanner.ExcludeCurrentTracks(plan, channel, installPath);
            InstallPlanner.MeasureWork(plan, channel, out planned, out need);
            free = InstallPathPolicy.FreeBytes(installPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The size line upstream paints on the setup panel, key for key
    /// (disk_about / disk_shortage), plus the shortage message as the status.</summary>
    public static (string Text, bool Danger) InstallDiskLine(ChannelManifest? channel, string installPath)
    {
        if (channel is null) return (Loc.Get("disk_unknown"), false);

        if (!TryMeasureInstallDisk(channel, installPath, out var planned, out var need, out var free))
            return (Loc.Get("disk_unread"), false);

        var plannedTxt = DownloadSize(planned);
        var needTxt = DownloadSize(need);
        if (free >= 0 && free < need)
        {
            var freeTxt = free <= 0 ? Loc.Get("disk_nospace") : DownloadSize(free);
            return (Loc.Format("disk_shortage", DownloadSize(need), plannedTxt, freeTxt), true);
        }

        return free >= 0
            ? (Loc.Format("disk_about", plannedTxt, needTxt, DownloadSize(free)), false)
            : (Loc.Format("disk_about_nodrive", plannedTxt, needTxt), false);
    }

    /// <summary>Whole-figure sizes for the disk line ("41.9 GB"), matching upstream.</summary>
    public static string DownloadSize(long bytes)
    {
        if (bytes <= 0) return Loc.Get("disk_small");
        var gb = bytes / 1_000_000_000.0;
        if (gb >= 1) return Loc.Format("size_gb", $"{gb:0.#}");
        var mb = bytes / 1_000_000.0;
        return mb >= 1 ? Loc.Format("size_mb", $"{mb:0}") : Loc.Get("disk_small");
    }

    /// <summary>Cheap, disk-only health check (no hashing) — what upstream's
    /// Check for updates does before it offers a download.</summary>
    [RelayCommand]
    private async Task CheckUpdates()
    {
        if (_contentBusy) return;
        try
        {
            var channel = await LoadChannelAsync();
            if (channel is null)
            {
                HealthText = "—";
                InstallStatus = Loc.Get("status_install_failed");
                return;
            }

            var health = Assess(channel, InstallPath);
            InstallStatus = HealthLine(health);
            InstallFraction = health.IsReady ? 100 : 0;
            AppendLog("Health: " + health.Summary);
            if (health.NeedsUpdate) AppendLog(Loc.Get("status_content_update_short"));
            if (health.Overall == InstallHealthStatus.Missing) AppendLog(Loc.Get("plain_install"));
        }
        catch (Exception ex)
        {
            AppendLog("Check for updates failed: " + ex.Message);
        }
    }

    /// <summary>
    /// One button for install / update / repair, because on the CAS path they are
    /// the same operation: reconcile the official file list against what is on
    /// disk and fetch what is missing or wrong.
    /// </summary>
    [RelayCommand]
    private async Task InstallGame()
    {
        if (_contentBusy)
        {
            AppendLog("A content job is already running.");
            return;
        }
        if (string.IsNullOrWhiteSpace(InstallPath))
        {
            InstallStatus = Loc.Get("status_pick_folder_install");
            return;
        }

        if (!TryInstallPreflight()) return;

        var gate = await RefreshDownloadGateAsync().ConfigureAwait(true);
        if (!LaneOpenForInstall(gate)) return;

        await RunContentAsync(Loc.Get("status_installing_game"), async (channel, progress, ct, run) =>
        {
            var state = await ContentInstallService.InstallAsync(
                channel,
                InstallMode.Full,
                InstallPath,
                fetcher: null,
                progress: progress,
                cancel: ct,
                runControl: run,
                decideOverlay: DecideOverlayEdits,
                allowContent: gate.Content,
                allowPlatform: gate.Platform,
                keepLocalFiles: KeepLocalFiles).ConfigureAwait(true);

            if (state.Incomplete || !string.IsNullOrWhiteSpace(state.LastError))
            {
                AppendLog("Content install incomplete: " + (state.LastError ?? "incomplete"));
                return (false, Loc.Get("status_install_incomplete"));
            }

            AppendLog($"Content install complete: client_ready={state.ClientReady} " +
                      $"server_ready={state.ServerReady} platform_ready={state.PlatformReady}");
            var health = Assess(channel, InstallPath);
            return (true, health.IsReady ? Loc.Get("ready_to_play") : HealthLine(health));
        }).ConfigureAwait(true);
    }

    /// <summary>Hashes every official file against the cached content manifests.
    /// Downloads nothing — that is what repair is for.</summary>
    [RelayCommand]
    private async Task Verify()
    {
        if (_contentBusy) return;
        await RunContentAsync(Loc.Get("phase_check"), async (channel, progress, ct, _) =>
        {
            var health = await ContentInstallService.VerifyExistingAsync(
                channel, InstallPath, progress, ct, keepLocalFiles: KeepLocalFiles, deep: true)
                .ConfigureAwait(true);

            RememberHealth(health);
            AppendLog("Verify: " + health.Summary);
            return (health.IsReady, health.IsReady ? Loc.Get("health_ready") : health.Summary);
        }).ConfigureAwait(true);
    }

    /// <summary>Fetches what is missing and replaces what is damaged, without
    /// touching files that verify. Mirrors upstream's repair action.</summary>
    [RelayCommand]
    private async Task RepairFiles()
    {
        if (_contentBusy) return;
        if (!TryInstallPreflight()) return;

        var gate = await RefreshDownloadGateAsync().ConfigureAwait(true);
        if (!LaneOpenForInstall(gate)) return;

        await RunContentAsync(Loc.Get("phase_check"), async (channel, progress, ct, run) =>
        {
            var before = Assess(channel, InstallPath);
            var health = await ContentInstallService.EnsureReadyAsync(
                channel,
                InstallPath,
                requireClient: true,
                requireServer: false,
                autoRepairCorrupt: true,
                autoApplyUpdates: before.NeedsUpdate,
                fetcher: null,
                progress: progress,
                cancel: ct,
                decideOverlay: DecideOverlayEdits,
                allowContent: gate.Content,
                allowPlatform: gate.Platform,
                keepLocalFiles: KeepLocalFiles).ConfigureAwait(true);

            RememberHealth(health);
            AppendLog("Repair: " + health.Summary);
            if (health.NeedsVerify)
                return (false, Loc.Get("health_unverified") + " — press Verify");

            return (health.IsReady || !health.Enforced,
                    health.IsReady ? Loc.Get("ready_to_play") : HealthLine(health));
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelInstall()
    {
        if (_installCts is null || _installCts.IsCancellationRequested) return;
        InstallStatus = Loc.Get("status_cancelling");
        AppendLog("Cancelling the content job…");
        _installCts.Cancel();
    }

    [RelayCommand]
    private void TogglePauseInstall()
    {
        if (_installRun is null) return;
        if (_installRun.IsPaused)
        {
            _installRun.Resume();
            InstallPaused = false;
            AppendLog("Download resumed.");
        }
        else
        {
            _installRun.Pause();
            InstallPaused = true;
            AppendLog(Loc.Get("status_paused"));
        }
    }

    /// <summary>
    /// Shared body of every content action: channel, cancellation, progress, the
    /// busy flag, the health memo and the status line. <paramref name="work"/>
    /// returns (ok, status).
    /// </summary>
    async Task RunContentAsync(
        string busyStatus,
        Func<ChannelManifest, IProgress<ContentInstallProgress>, CancellationToken, InstallRunControl,
            Task<(bool Ok, string Status)>> work)
    {
        if (string.IsNullOrWhiteSpace(InstallPath))
        {
            InstallStatus = Loc.Get("status_pick_folder_install");
            return;
        }
        if (!Directory.Exists(InstallPath))
        {
            try { Directory.CreateDirectory(InstallPath); }
            catch (Exception ex)
            {
                InstallStatus = Loc.Format("status_error", ex.Message);
                AppendLog("Cannot use " + InstallPath + ": " + ex.Message);
                return;
            }
        }

        var channel = await LoadChannelAsync();
        if (channel is null)
        {
            InstallStatus = Loc.Get("status_install_failed");
            return;
        }

        _contentBusy = true;
        // Nothing of the last job may sit on the card: a repair that follows a
        // download would otherwise open showing the download's phase and file
        // (upstream's ShowSimpleInstallBar(false) clears the same slots).
        ClearInstallCard();
        Installing = true;
        InstallPaused = false;
        InstallStatus = busyStatus;
        _installCts = new CancellationTokenSource();
        _installRun = new InstallRunControl();

        try
        {
            ApplyTransferLimits();
            Save();
            var (ok, status) = await work(channel, new InstallProgressRelay(this), _installCts.Token, _installRun)
                .ConfigureAwait(true);
            InstallStatus = status;
            InstallFraction = ok ? 100 : InstallFraction;
            if (ok) HealthText = HealthLine(Assess(channel, InstallPath));
        }
        catch (OperationCanceledException)
        {
            AppendLog(Loc.Get("status_content_cancelled"));
            InstallStatus = Loc.Get("status_download_cancelled");
        }
        catch (Exception ex)
        {
            AppendLog("Content job failed: " + ex.Message);
            InstallStatus = Loc.Format("status_error", ex.Message);
        }
        finally
        {
            _contentBusy = false;
            Installing = false;
            InstallPaused = false;
            _installCts?.Dispose();
            _installCts = null;
            _installRun = null;
        }
    }

    /// <summary>
    /// Refuses a download the master server has switched off, before anything is
    /// fetched. Windows asks the same two questions and then puts up a dialog;
    /// the Linux shell writes the same string to the status line and the log,
    /// because a view model has no window to open one on.
    /// </summary>
    internal bool LaneOpenForInstall(Lanes gate) => LaneOpenForInstall(gate, InstallPath);

    /// <summary>The root is a parameter so the two questions can be answered for
    /// a folder other than the configured one (and driven by a test).</summary>
    internal bool LaneOpenForInstall(Lanes gate, string root)
    {
        if (!gate.AnyLane)
        {
            InstallStatus = Loc.Get("status_downloads_off");
            AppendLog(Loc.Get("msg_downloads_off"));
            return false;
        }
        if (!gate.Content && !LinuxSettings.LooksLikeInstall(root))
        {
            InstallStatus = Loc.Get("status_downloads_off");
            AppendLog(Loc.Get("msg_downloads_off_content"));
            return false;
        }
        return true;
    }

    /// <summary>Refuses a job that cannot fit, before it starts writing. Upstream
    /// blocks on the same comparison (free &lt; need).</summary>
    bool TryInstallPreflight()
    {
        try
        {
            Directory.CreateDirectory(InstallPath);
        }
        catch (Exception ex)
        {
            InstallStatus = Loc.Format("status_error", ex.Message);
            AppendLog("Cannot create " + InstallPath + ": " + ex.Message);
            return false;
        }

        ApplyTransferLimits();
        var (text, danger) = InstallDiskLine(_channel, InstallPath);
        InstallSizeText = text;
        if (danger)
        {
            InstallStatus = text;
            AppendLog("Refusing to start: " + text);
            return false;
        }

        if (TryMeasureInstallDisk(_channel, InstallPath, out _, out var need, out _) && need > 0)
            AppendLog($"Content job size: {DownloadSize(need)} to fetch — {text}");
        return true;
    }

    InstallHealthReport Assess(ChannelManifest channel, string root) =>
        ContentInstallService.Assess(channel, root, requireClient: true, requireServer: false,
            keepLocalFiles: KeepLocalFiles);

    void RememberHealth(InstallHealthReport health)
    {
        _healthMemo = health;
        _healthMemoRoot = InstallPath;
        _healthMemoUtc = DateTime.UtcNow;
        HealthText = HealthLine(health);
    }

    /// <summary>Cached health for the UI, so a repaint does not re-assess the tree.</summary>
    public InstallHealthReport? HealthForUi(string? root)
    {
        root ??= "";
        if (_healthMemo is not null
            && string.Equals(_healthMemoRoot, root, StringComparison.Ordinal)
            && (DateTime.UtcNow - _healthMemoUtc).TotalSeconds < 5)
        {
            return _healthMemo;
        }
        if (_channel is null)
            return null;
        try
        {
            return Assess(_channel, root);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Upstream's health wording, key for key.</summary>
    public static string HealthLine(InstallHealthReport? health)
    {
        if (health is null) return "—";
        if (health.Overall == InstallHealthStatus.Corrupted)
        {
            return health.BrokenFileCount > 0
                ? Loc.Format("health_missing_n", health.BrokenFileCount)
                : Loc.Get("health_corrupt");
        }
        if (health.Overall == InstallHealthStatus.Unverified) return Loc.Get("health_unverified");
        if (!health.Enforced) return Loc.Get("health_local");
        if (health.Overall == InstallHealthStatus.Ready && health.HasOverlayEdits)
            return Loc.Format("health_scripts_differ", health.OverlayEditCount);
        return health.Overall switch
        {
            InstallHealthStatus.Ready => Loc.Get("health_ready"),
            InstallHealthStatus.UpdateAvailable => Loc.Get("health_update"),
            InstallHealthStatus.Incomplete => Loc.Get("health_incomplete"),
            InstallHealthStatus.Missing => Loc.Get("health_missing"),
            _ => health.Summary,
        };
    }

    /// <summary>
    /// What to do with the script trees a mod has edited when official content is
    /// written over them. Non-interactive on purpose: the Linux shell has no modal
    /// overlay prompt yet, and upstream's default in that situation is to keep the
    /// edits rather than silently overwrite a user's scripts.
    /// </summary>
    static OverlayExtractPolicy DecideOverlayEdits(OverlayEditReport report)
        => report.HasEdits ? OverlayExtractPolicy.KeepEdits : OverlayExtractPolicy.WriteOfficial;

    /// <summary>Maps engine progress onto the bar and the status line, with a
    /// smoothed rate and an ETA (upstream's ApplyInstallProgress, condensed).
    ///
    /// <para>Internal because the suite drives it with a synthetic tick: the card
    /// having rows is not the same claim as the rows having a download in them, and
    /// only a tick can tell those apart.</para></summary>
    internal sealed class InstallProgressRelay : IProgress<ContentInstallProgress>
    {
        readonly MainViewModel _vm;
        long _markBytes;
        DateTime _markUtc = DateTime.UtcNow;
        DateTime _lastUiUtc = DateTime.MinValue;
        string _markKey = "";
        double _rate;

        public InstallProgressRelay(MainViewModel vm) => _vm = vm;

        public void Report(ContentInstallProgress p)
        {
            var now = DateTime.UtcNow;
            var file = !string.IsNullOrWhiteSpace(p.FileName) ? p.FileName! : p.Message ?? "";
            var bytes = p.Unit switch
            {
                ProgressUnit.Bytes => true,
                ProgressUnit.Items => false,
                _ => p.Total > 1_000_000,
            };

            // A scan counts files in Current and bytes read in JobCurrent, so the
            // rate comes off a different counter than the bar does.
            var scanning = p.Unit == ProgressUnit.Items && p.JobTotal > 0;
            var rateBytes = scanning ? p.JobCurrent : p.Current;
            var hasRate = bytes || scanning;

            var pct = scanning
                ? Math.Min(100.0, 100.0 * p.JobCurrent / p.JobTotal)
                : p.Total > 0
                    ? Math.Min(100.0, 100.0 * p.Current / p.Total)
                    : 0;

            // Keyed on the phase, not the file: several objects transfer at once
            // and the name changes every tick, which would restart the rate window
            // before it ever produced a number.
            var markKey = p.Unit == ProgressUnit.Unknown
                ? file
                : (p.Phase ?? "") + "|" + p.Track;
            if (!string.Equals(markKey, _markKey, StringComparison.Ordinal))
            {
                _markKey = markKey;
                _markBytes = rateBytes;
                _markUtc = now;
                _rate = 0;
            }
            else if (rateBytes < _markBytes)
            {
                _markBytes = rateBytes;
                _markUtc = now;
            }
            else if (hasRate && (now - _markUtc).TotalSeconds >= 0.4)
            {
                var dt = (now - _markUtc).TotalSeconds;
                var inst = (rateBytes - _markBytes) / dt;
                _rate = _rate <= 0 ? inst : (_rate * 0.65) + (inst * 0.35);
                _markBytes = rateBytes;
                _markUtc = now;
            }

            var phase = PhaseLabel(p.Phase);
            var step = p.StepCount > 0 ? Loc.Format("part_of", Math.Max(1, p.StepIndex), p.StepCount) : "";
            if (!string.IsNullOrWhiteSpace(p.Track))
                step = step.Length == 0 ? p.Track : step + "  ·  " + p.Track;

            var detail = "";
            if (p.Unit == ProgressUnit.Items)
            {
                if (p.ItemsTotal > 0)
                    detail = Loc.Format("files_progress", p.ItemsDone, p.ItemsTotal);
                if (p.JobTotal > 0)
                {
                    var checkedLine = $"{Bytes(p.JobCurrent)} / {Bytes(p.JobTotal)}";
                    detail = detail.Length == 0 ? checkedLine : detail + "  ·  " + checkedLine;
                }
            }
            else if (bytes && p.Total > 0)
            {
                detail = $"{Bytes(p.Current)} / {Bytes(p.Total)}";
                var remain = p.JobTotal > p.JobCurrent ? p.JobTotal - p.JobCurrent
                    : p.Total > p.Current ? p.Total - p.Current : 0;
                if (_rate > 1 && remain > 0)
                    detail += "  ·  " + Loc.Format("eta_remaining", Eta(remain / _rate));
            }
            else if (p.Total > 0)
            {
                detail = $"{p.Current} / {p.Total}";
            }

            // The rate is held apart from the detail line so the card can put it in
            // a row of its own; the status sentence appends it in the same place it
            // always did, so nothing the advanced card or the log shows moves.
            var rateLine = _rate > 1 && bytes ? Rate(_rate) : "";

            // The card's file row follows upstream: a scan counts files, so it
            // reads "412 of 900 files" rather than one of the names it is reading.
            var fileLine = p.Unit != ProgressUnit.Items && p.ItemsTotal > 0
                ? Loc.Format("files_progress", p.ItemsDone, p.ItemsTotal)
                : file;

            // And its bytes row is bytes, always: a scan counts files in Current
            // and bytes read in JobCurrent, so the checked figures are what belong
            // there (the file row above already carries the count).
            var cardBytes = p.Unit == ProgressUnit.Items
                ? p.JobTotal > 0 ? $"{Bytes(p.JobCurrent)} / {Bytes(p.JobTotal)}" : ""
                : p.Total > 0 ? $"{Bytes(p.Current)} / {Bytes(p.Total)}" : "";

            // Overall percent spans the steps, so the bar does not restart at each
            // track boundary.
            var overall = p.StepCount > 0
                ? Math.Min(100.0, 100.0 * Math.Max(0, p.StepIndex - 1) / p.StepCount
                                  + (pct / 100.0) * (100.0 / p.StepCount))
                : pct;

            var uiDue = (now - _lastUiUtc).TotalMilliseconds >= 80
                        || p.Current >= p.Total
                        || string.Equals(p.Phase, "done", StringComparison.OrdinalIgnoreCase);
            if (!uiDue) return;
            _lastUiUtc = now;

            var status = phase;
            if (step.Length > 0) status += "  ·  " + step;
            if (file.Length > 0) status += "  ·  " + file;
            if (detail.Length > 0) status += "  ·  " + detail;
            if (rateLine.Length > 0) status += "  ·  " + rateLine;

            var active = p.Active;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _vm.InstallFraction = overall;
                _vm.InstallPhase = phase;
                _vm.InstallFileLine = fileLine;
                _vm.InstallBytes = cardBytes;
                _vm.InstallStep = step;
                _vm.InstallRateText = rateLine;
                _vm.SetInstallTransfers(active);
                if (!_vm.InstallPaused)
                    _vm.InstallStatus = status;
            });
        }

        static string PhaseLabel(string? phase) => (phase ?? "").Trim().ToLowerInvariant() switch
        {
            "download" or "copy" or "http" or "reget" or "fetch" => Loc.Get("phase_download"),
            "scan" or "recheck" or "verify" or "check" or "repair" or "sweep" => Loc.Get("phase_check"),
            "unpack" => Loc.Get("phase_unpack"),
            "manifest" => Loc.Get("phase_manifest"),
            "done" => Loc.Get("phase_done"),
            "" => Loc.Get("working"),
            _ => char.ToUpperInvariant(phase![0]) + phase[1..],
        };

        /// <summary>Decimal units, matching the published sizes (upstream makes the
        /// same point: showing GiB under a "GB" label makes one install read two
        /// different numbers).</summary>
        internal static string Bytes(long n) => FormatBytes(n);

        static string Rate(double bytesPerSecond) => Bytes((long)bytesPerSecond) + "/s";

        static string Eta(double seconds)
        {
            if (seconds < 60) return Loc.Format("eta_s", (int)Math.Ceiling(seconds));
            if (seconds < 3600)
            {
                var m = (int)(seconds / 60);
                return Loc.Format("eta_ms", m, (int)(seconds - (m * 60)));
            }
            var h = (int)(seconds / 3600);
            return Loc.Format("eta_hm", h, (int)((seconds - (h * 3600)) / 60));
        }
    }

    /// <summary>Decimal byte formatting, shared with the size line above the bar.</summary>
    internal static string FormatBytes(long n)
    {
        if (n < 1000) return Loc.Format("size_b", n);
        double v = n;
        var unit = -1;
        do
        {
            v /= 1000.0;
            unit++;
        } while (v >= 1000 && unit < 3);

        var key = new[] { "size_kb", "size_mb", "size_gb", "size_tb" }[Math.Max(0, unit)];
        return Loc.Format(key, v < 10 ? v.ToString("0.00") : v < 100 ? v.ToString("0.0") : v.ToString("0"));
    }

    static string Bytes(long n) => FormatBytes(n);

    static string Describe(ChannelTrackTip tip)
    {
        var lanes = new List<string>();
        if (tip.ContentManifestUrl is not null) lanes.Add("cas");
        if (tip.HasPatchChain) lanes.Add("patch chain");
        if (tip.Assets.Count > 0) lanes.Add($"{tip.Assets.Count} volume(s)");
        var size = tip.TotalBytes is long b and > 0 ? FormatBytes(b) : "size unlisted";
        return lanes.Count == 0 ? size : size + " · " + string.Join(" + ", lanes);
    }
}
