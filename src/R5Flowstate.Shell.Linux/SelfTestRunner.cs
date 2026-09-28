using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using R5Flowstate.Content;
using R5Flowstate.Content.Rpak;
using R5Flowstate.Linux.Core;
using R5Flowstate.Shell.Linux.ViewModels;

namespace R5Flowstate.Shell.Linux;

/// <summary>Headless verification pass (Program --selftest). Instantiates the
/// real MainWindow so every XAML resource, template and binding resolves, then
/// exercises the EA-detect logic on a throwaway prefix. Prints PASS/FAIL lines
/// and returns a process exit code. Never touches the user's real prefix and
/// never installs anything.</summary>
static class SelfTestRunner
{
    public static int Run(Window window)
    {
        int failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " — " + detail : "")}");
            if (!ok) failures++;
        }

        // The player's settings file is read-only to this suite, held around the
        // whole run and put back if any lane writes to it.
        //
        // <para>There is already a per-lane repair, and it is the right shape for a
        // lane that knows what it disturbed: `ConsoleSelfTest.SteeringChecks` drives
        // the mode combo and the map combo — both of which call the real `_s.Save()` —
        // and rewrites the file from the bytes it read. Two things make that repair
        // less than the guarantee this needs to be. It runs at the **tail of the
        // group's `try`**, not in its `finally` (that one restores the art seam only),
        // so anything thrown between the last save and the repair leaves the player's
        // file changed with no net under it. And it can only repair what it knows
        // about: the next lane to call a saving handler — `SyncNotesUnread(markSeen:
        // _notesTabActive)` → `_s.Save()` is one, and it is the whole settings file it
        // writes, not a stamp — arrives with nobody watching. It also explains the
        // mtime: that repair writes unconditionally, so the file's timestamp moves on
        // every run even when its bytes do not.</para>
        //
        // <para>So the whole-file baseline, taken before the first check runs and
        // restored in the `finally` — even when a check crashed. A lane's own repair
        // stays where it is: this is the net under it, not a replacement. What it
        // cannot do is stop the write happening — a lane that saves still writes, and
        // SteeringChecks proves it by rewriting the file itself — so the promise is the
        // **bytes**, which is what is compared, and not the mtime.</para>
        var settingsBefore = ReadSettingsBytes();

        try
        {
            // 1. Version is Linux-2.00 and Windows Velopack can never see it.
            var vm = (MainViewModel)window.DataContext!;
            Check("version == Linux-2.00", vm.Version == "Linux-2.00", $"got '{vm.Version}'");

            // 2. The ported UI actually built: the named surface the theme and
            //    code-behind depend on exists after XAML load + template apply.
            string[] names =
            {
                "RootChrome", "BtnQuickPlay", "BtnModeToggle", "BtnHeaderOpenFolder", "BtnHeaderConsole",
                "BtnTabLocal", "BtnTabServers", "BtnTabLeaderboards", "BtnTabConsole", "BtnTabMods",
                "BtnTabBlog", "BtnTabNotes", "BtnTabCredits", "TabScroll", "BtnBrowserRefresh",
                "PanelSimple", "PanelSimpleLocal", "PanelSimpleSetup", "PanelSimplePlay", "ListModes",
                // The setup card's CTA. It was never asserted, which is how it shipped
                // bound to PLAY — a caption that says INSTALL and answers "the game is
                // not installed there".
                "BtnSimpleSetupInstall",
                // The setup card's own progress card, which the port had left behind
                // entirely: upstream's form came over and upstream's PanelSimpleInstallProgress
                // did not, so a download in simple mode moved nothing on the install tab.
                "PanelSimpleInstallProgress", "TxtSimpleProgressPhase", "TxtSimpleProgressFile",
                "TxtSimpleProgressBytes", "TxtSimpleProgressStep", "TxtSimpleProgressRate",
                "BarSimpleFile", "BarSimpleInstall", "ListActiveTransfers",
                "BtnSimpleInstallPause", "BtnSimpleInstallCancel",
                "ImgLoadscreen", "TxtHeroTitle", "TxtHeroSubtitle", "TxtHeroCommand",
                // The hero's two 36x36 buttons. They were both missing from this list
                // and one of them was missing from the window: RefreshLoadscreenChrome
                // computed CanPickMap and CanShuffleLoadscreen and nothing bound either,
                // so there was no way to pick a map and the shuffle button was drawn for
                // every mode instead of the lobby. Bound is what the checks below add.
                "BtnMapPicker", "BtnReloadLoadscreen",
                "PanelMapPicker", "ListMapTiles", "BarSimplePlay", "ChkSimpleCheats", "ChkUseDx12",
                "TxtClientWidth", "TxtClientHeight", "BtnPlay", "BtnChangeMap", "BtnLaunchArgs",
                "PanelSimpleLeaderboards", "ListLb", "BtnLbViewBoard", "BtnLbViewMatches", "CmbLbSeason",
                "TxtLbActivity", "TxtLbSearch", "BtnLbPrev", "BtnLbNext",
                "BtnLbColRank", "BtnLbColPlayer", "BtnLbColScore", "BtnLbColK", "BtnLbColD",
                "BtnLbColKd", "BtnLbColDmg", "BtnLbColAcc", "BtnLbColWpn", "BtnLbColIn",
                "BtnLbColHs", "BtnLbColHits", "BtnLbColWins", "BtnLbColGames", "BtnLbColWr",
                "BtnLbColStreak", "ScrollLbBoard", "ScrollLbHistory", "ListLbHistory",
                "PanelLbDetail", "TxtLbDetailPersona", "TxtLbDetailRank", "TxtLbDetailStats",
                "TxtLbDetailLoadout", "TxtLbDetailStreak", "TxtLbDetailBest",
                "BtnLbFullHistory", "ListLbMaps", "ListLbWeapons", "ListLbMatches",
                "TxtLbMatchesStatus", "OverlayLbMatch", "TxtMatchTitle", "TxtMatchScore",
                "TxtMatchMeta", "TxtMatchHost", "TxtMatchWinner", "TxtMatchStatus",
                "TxtMatchOneSided", "ListMatchPlayers",
                "PanelSimpleServers", "ListServersSimple", "BtnDisconnect",
                "PanelSimpleBlog", "TxtBlogStatus", "PanelBlogList", "ScrollBlogList", "ListBlog",
                "PanelBlogReader", "BtnBlogBack", "BtnBlogOpenSite", "TxtBlogReaderStatus",
                "ScrollBlogReader", "GridBlogHeader", "ImgBlogCover", "RectBlogCoverFade",
                "BorderBlogTag", "TxtBlogTag", "TxtBlogDate", "TxtBlogTitle", "TxtBlogSummary",
                "BlogBody", "DotBlogUnread",
                "PanelSimpleNotes", "ListNotes", "BtnNotesGame", "BtnNotesLauncher",
                "PanelSimpleMods", "ListModsInstalled", "ListModsBrowse", "TxtModsStatus", "BtnModsInstallFile",
                "PanelSimpleCredits", "ListCredits",
                "PanelSimpleConsole", "TxtConsoleServer", "TxtConsoleClient", "TxtConsoleServerCmd",
                "TxtConsoleClientCmd", "BtnConsoleClear",
                // The console tab's mode/map pickers and its action button. None of
                // the three was in this list, which is how they shipped bound wrong
                // (the gamemode list rendering a type name, Stop killing one role):
                // presence is the first half, and CheckStopWiring's identity checks
                // are the second.
                "CmbConsoleMode", "CmbConsoleMap", "BtnConsoleAction",
                "PanelSimpleSettings", "CmbUiLanguage", "TxtInstallRoot", "CmbProton", "TxtPrefixPath",
                "TxtEaStatus", "BtnDownloadEa", "BtnDownloadProton", "TxtDownloadStatus", "BarDownload",
                "BtnCreateShortcuts",
                // The EA rows, now that there is one runtime: the two switches, the
                // channel probe, the prep, and the two verdict lines they feed.
                "ChkStartEaWithGame", "TxtEaChannel", "BtnEaProbe", "BtnPrepareEaPrefix",
                "ChkAutoGe", "TxtEaBridgeStatus", "TxtEaIdentity",
                "BarInstall", "TxtInstallStatus", "BtnCheckUpdates", "BtnPauseInstall", "BtnCancelInstall",
                "TxtChannelStatus", "TxtInstallSize",
                "PanelAdvanced", "ListServersAdvanced", "BtnLaunchClient", "BtnLaunchDedi",
                "RowDediPackage", "BtnDediPackage",
                "CmbPlaylist", "CmbMap", "TxtDediPort", "TxtDediPassword", "TxtClientPreview",
                "TxtDediPreview", "TxtLog",
                "BarSimpleFooter", "TxtSimpleStatus", "DotSimpleStatus", "BarAdvancedStatus", "TxtStatus",
            };
            foreach (var n in names)
                Check($"control '{n}'", window.FindControl<Control>(n) is not null);

            // The hero's two buttons, and the rule that decides whether each is
            // drawn. Present-but-unbound is the exact shape of the bug the player
            // reported ("you cannot change maps as there's not buttons to do it"):
            // the properties existed, the picker panel existed, the four loc keys
            // existed, and no button opened it. Asserting IsVisible == the property
            // is what a binding means; on a headless window nothing has selected a
            // mode yet, so both sides are false and the assertion is that they agree,
            // not that they are true.
            var pickerButton = window.FindControl<Button>("BtnMapPicker");
            var shuffleButton = window.FindControl<Button>("BtnReloadLoadscreen");
            Check("the map picker button is drawn exactly when the card has maps to pick",
                pickerButton is not null && pickerButton.IsVisible == vm.CanPickMap,
                pickerButton is null ? "no button" : $"visible={pickerButton.IsVisible}, CanPickMap={vm.CanPickMap}");
            Check("the shuffle button is drawn exactly for the lobby",
                shuffleButton is not null && shuffleButton.IsVisible == vm.CanShuffleLoadscreen,
                shuffleButton is null ? "no button" : $"visible={shuffleButton.IsVisible}, CanShuffleLoadscreen={vm.CanShuffleLoadscreen}");

            // The picker button's handler is the ported one, and it has to be a
            // handler rather than a command: OnToggleMapPicker flips PanelMapPicker
            // and calls EnsureMapTiles() on the open, which is upstream's lazy tile
            // decode. A button with no Click is a button that does nothing.
            //
            // Read from the source rather than by raising the click. Raising Click
            // would run OnToggleMapPicker -> EnsureMapTiles(), and on a box where the
            // game is installed that starts the art host inside a Proton prefix: a
            // selftest that spawns a decoder is not a selftest. The suite already
            // reads the source tree for the loc keys, so the wiring is read the same
            // way, and the closed panel is asserted from the loaded window below.
            CheckPickerHandler(Check);
            Check("the map picker starts closed",
                window.FindControl<Control>("PanelMapPicker")?.IsVisible == false);

            // The hero lane's own bookkeeping — the guard that was missing, and the
            // wording for a batch the launcher walked away from. Runs before the
            // window is shown, and puts the selection back where it found it.
            CheckLoadscreenQueue(vm, Check);

            // The Res row: the mode the launcher will send has to be one of the three
            // and it has to be visible on the launch line the player reads. A settings
            // file with no mode in it (every file written before this existed) is the
            // case that matters — it loads as fullscreen, not as "no flag".
            var expectedFlag = vm.ClientWindowMode switch
            {
                "windowed" => "-windowed",
                "borderless" => "-windowed -noborder",
                "fullscreen" => "-fullscreen",
                _ => "(not a window mode at all)",
            };
            Check("the client's window mode is one of the three and shows on the launch line",
                vm.ClientWindowMode is "windowed" or "borderless" or "fullscreen"
                && vm.ClientPreview.Contains(expectedFlag, StringComparison.Ordinal),
                $"mode '{vm.ClientWindowMode}' -> {expectedFlag}; preview: {vm.ClientPreview}");

            // Both Res buttons open the menu, and both dimension boxes commit on
            // LostFocus. Read from the source for the same reason the picker's Click
            // is: opening the menu is a UI gesture the headless suite cannot make, and
            // the handler name is what a rename would break.
            CheckResolutionWiring(Check);

            // The hosted console's two halves live in two projects that cannot
            // reference each other (the relay is net8.0-windows; Linux.Core is not),
            // so what keeps them agreed is these source reads.
            CheckConsoleContract(Check);

            // And the other half of the same story: the relay and the art host are
            // two trimmed self-contained publishes, so they need two folders, and
            // install.sh is the place that decides. EaSelfTest covers the locator's
            // half of the rule; this covers the script's.
            CheckInstallLayout(Check);

            // What the console tab's three broken controls are wired to now. Present
            // is not wired, and all three were present: the mode combo rendered a
            // type name, Stop ran the dedi's own kill, and Clear ran the dedi's own
            // clear. CheckStopWiring pins the commands; ConsoleSelfTest's StopChecks
            // pins what those commands then do.
            CheckStopWiring(Check, vm, window);

            // The helpers' PE subsystem, and the two stdout writes that had to stop
            // mattering once they became GUI binaries. The csprojs live in the source
            // tree, so this is where that half is read.
            CheckHelperSubsystems(Check);

            // The net under every lane that saves: the player's settings file goes back
            // byte for byte. Proved against a file of its own — see the check.
            CheckSettingsNet(Check);

            // Present is not wired: the Settings button has to reach the command
            // that writes the entries, or it is a button that does nothing. Its
            // own execution is covered by DesktopSelfTest, in temp folders.
            var shortcutsButton = window.FindControl<Button>("BtnCreateShortcuts");
            Check("the shortcuts button is wired to its command",
                shortcutsButton is not null && ReferenceEquals(shortcutsButton.Command, vm.CreateShortcutsCommand),
                shortcutsButton?.Command?.ToString() ?? "no command");

            // The setup card's CTA. The card is only on screen while the game is
            // missing, so this button is always the Install half of Windows'
            // mode-switching CTA (upstream: OnPlay -> _simplePlayKind ->
            // RunInstallAsync(Full)). Bound to PLAY it did nothing at all, and said
            // "the game is not installed there" — true, and useless.
            var setupInstall = window.FindControl<Button>("BtnSimpleSetupInstall");
            Check("the setup card's INSTALL runs the install, not PLAY",
                setupInstall is not null && ReferenceEquals(setupInstall.Command, vm.InstallGameCommand)
                && !ReferenceEquals(setupInstall.Command, vm.PlayLocalCommand),
                setupInstall?.Command?.ToString() ?? "no command");

            // Every loc key the port names must resolve. Loc.Get returns the key
            // itself when it is missing, so a typo or an unported string reaches the
            // player as `status_install_needed_to_play` in the status bar — which is
            // exactly what happened. Needs the source tree, so an installed binary
            // reports the skip rather than a false pass.
            CheckLocKeys(Check);

            // The EA controls that are left, wired. The probe button must reach the
            // probe — a probe button that does nothing is worse than no button,
            // because it reads as "the channel is fine".
            var eaProbeButton = window.FindControl<Button>("BtnEaProbe");
            var prepareButton = window.FindControl<Button>("BtnPrepareEaPrefix");
            Check("the EA controls are wired to their commands",
                eaProbeButton is not null && ReferenceEquals(eaProbeButton.Command, vm.ProbeEaChannelCommand)
                && prepareButton is not null && ReferenceEquals(prepareButton.Command, vm.PrepareEaPrefixCommand));

            // One prefix, and everything EA-facing reads it. This replaced three
            // checks (the runtime combo's selected item, the EA prefix box, and the
            // bridge buttons' commands) whose subjects no longer exist: the assertion
            // that survives is the one that matters — `EaPrefixFor` is the game's
            // prefix and nothing else, so an EA install and the game cannot disagree
            // about which prefix they are in.
            Check("the EA prefix is the game's prefix, and there is no second one",
                vm.EaPrefixFor() == vm.PrefixPath && vm.EaPrefixFor().Length > 0,
                vm.EaPrefixFor());

            Check("the channel and identity verdict lines are bound to the view model",
                window.FindControl<TextBlock>("TxtEaBridgeStatus")?.Text == vm.EaBridgeStatus
                && window.FindControl<TextBlock>("TxtEaIdentity")?.Text == vm.EaIdentityStatus,
                vm.EaBridgeStatus.Length == 0 ? "(channel line empty)" : vm.EaBridgeStatus);

            // The rows the split needed, gone from the window rather than left
            // disabled. A disabled combo is still a promise that a runtime can be
            // chosen; a missing one is the truth.
            var gone = new[] { "CmbEaRuntime", "TxtEaPrefixPath", "TxtWineStatus", "BtnEaBridge", "BtnEaUnbridge" };
            var stillThere = gone.Where(n => window.FindControl<Control>(n) is not null).ToList();
            Check($"the {gone.Length} split-runtime controls are gone from the window",
                stillThere.Count == 0,
                stillThere.Count == 0 ? "none present" : string.Join(", ", stillThere));

            // 3. VM catalogs that drive the lists.
            // The rail is the ported mode-card layer now: the groups are the
            // cards' own groups and the map grid is the selected card's maps, so
            // these hold whether or not the install's catalog has loaded yet.
            var railItems = vm.Modes.SelectMany(g => g.Items).ToList();
            Check("rail groups hold the mode cards",
                vm.Modes.Count >= 1 && railItems.Count == vm.ModeCards.Count
                && vm.Modes.All(g => g.Items.Count > 0 && g.Header.Length > 0),
                $"{vm.Modes.Count} group(s), {vm.ModeCards.Count} card(s)");
            Check("a mode card is selected and the hero reads it",
                vm.SelectedMode is not null && vm.SelectedMode.IsSelected
                && railItems.Any(i => i.IsSelected)
                && vm.HeroTitle == vm.SelectedMode.Title,
                $"'{vm.HeroTitle}'");
            Check("map tiles are the selected mode's maps",
                vm.SelectedMode is not null && vm.MapTiles.Count == vm.SelectedMode.Maps.Count
                && vm.MapTiles.Count > 0 && vm.MapTiles.All(t => t.Option is not null),
                $"{vm.MapTiles.Count} tile(s) for '{vm.SelectedMode?.Id}'");
            Check("credits populated", vm.CreditsRows.Count >= 7, $"{vm.CreditsRows.Count} entries");
            Check("languages populated", vm.Languages.Count == 12, $"{vm.Languages.Count} languages");
            Check("language picker matches NoticeLanguages", vm.Languages.Count == NoticeLanguages.Picker.Length);
            Check("selected language is in the picker",
                vm.SelectedLanguage is not null && vm.Languages.Contains(vm.SelectedLanguage),
                vm.SelectedLanguage?.Code ?? "null");
            Check("loc table resolves a key", Loc.Get("play").Length > 0, Loc.Get("play"));
            Check("hero bound", vm.HeroTitle.Length > 0, vm.HeroTitle);

            // 4. Launch arg builders mirror Windows semantics.
            var client = LaunchArgBuilder.BuildClient(dev: true, offlineNoAuth: true, forceOnline: false,
                language: "english", extra: "-pretend", windowMode: "borderless", width: 1920, height: 1080,
                map: "mp_lobby", password: "pw", connect: "127.0.0.1:37015");
            Check("client args contain -devsdk", client.Contains("-devsdk"));
            Check("client args contain -offline", client.Contains("-offline"));
            Check("client args contain +connect", client.Contains("+connect"));
            var dedi = LaunchArgBuilder.BuildDedi(dev: true, offlineNoAuth: true, hostOnline: false,
                port: 37015, playlist: "survival_dev", map: "mp_lobby", password: "pw", cheats: true,
                extra: null);
            Check("dedi args contain -dedicated", dedi.Contains("-dedicated"));
            Check("dedi args contain +sv_password", dedi.Contains("+sv_password"));

            // 5. EA-detect logic on a throwaway prefix (never the user's).
            var tmp = Path.Combine(Path.GetTempPath(), "r5f-selftest-prefix-" + Guid.NewGuid().ToString("N"));
            try
            {
                Check("EA detect: empty prefix → not installed", !ProtonLauncher.IsEaInstalled(tmp));

                var eaExe = Path.Combine(tmp, "drive_c", "Program Files", "Electronic Arts", "EADesktop.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(eaExe)!);
                File.WriteAllText(eaExe, "stub");
                var found = ProtonLauncher.FindEaDesktopExe(tmp);
                Check("EA detect: stubbed EADesktop.exe found", found is not null, found ?? "null");
                Check("EA detect: IsEaInstalled true with stub", ProtonLauncher.IsEaInstalled(tmp));
                Check("EA detect: found path is the stub", found == eaExe);
            }
            finally
            {
                try { Directory.Delete(tmp, recursive: true); } catch { }
            }

            // 6. Settings round-trip is side-effect free under a temp HOME.
            var json = System.Text.Json.JsonSerializer.Serialize(new LinuxSettings());
            Check("settings serialize", json.Contains("ProtonDir"));

            // 6c. Legal-notice lane: the Servers gate depends on this parse, so
            //     a malformed payload must fail loudly rather than unlock the tab.
            var notice = NoticeClient.ParseEula(
                """{"success":true,"data":{"contents":"Terms of use.","version":4,"lang":"german"}}""");
            Check("notice parse: success", notice.Success, notice.Error ?? "ok");
            Check("notice parse: contents", notice.Contents == "Terms of use.", notice.Contents);
            Check("notice parse: version", notice.Version == 4, notice.Version.ToString());
            Check("notice parse: lang", notice.Lang == "german", notice.Lang);
            Check("notice parse: rejects empty contents",
                !NoticeClient.ParseEula("""{"success":true,"data":{"contents":"","version":4}}""").Success);
            Check("notice parse: rejects missing version",
                !NoticeClient.ParseEula("""{"success":true,"data":{"contents":"x"}}""").Success);
            Check("notice parse: rejects non-JSON", !NoticeClient.ParseEula("<html>502</html>").Success);
            Check("notice url: https default",
                NoticeClient.NormalizeBaseUrl(null) == "https://play.r5flowstate.org",
                NoticeClient.NormalizeBaseUrl(null));
            Check("notice lang: unknown code falls back to english",
                NoticeLanguages.Sanitize("klingon") == "english");
            Check("notice lang: UI pick is an allowed code",
                NoticeLanguages.IsAllowed(vm.EulaUiLanguage()), vm.EulaUiLanguage());
            Check("notice gate: nothing accepted on a fresh config",
                vm.EulaVersionAccepted >= 0, $"accepted v{vm.EulaVersionAccepted}");

            // 6d. The agreement dialog itself: with a prefetched notice it must
            //     paint the text and enable Accept (requireAccept) or hide the
            //     accept/decline pair (read-only). No network, no settings write.
            try
            {
                var prefetched = new EulaResult
                {
                    Success = true,
                    // Shape of the real notice (opening of the live payload):
                    // multi-paragraph and long enough to wrap and scroll, which
                    // is where a legal body usually looks broken.
                    Contents =
                        """
                        R5Flowstate Privacy Policy
                        Last Modified: 18 September 2026 at 23:37 UTC

                        R5Flowstate USER NOTICE

                        This build is an unofficial community client. It is not affiliated with, endorsed by, or supported by Electronic Arts, Respawn Entertainment, or Apex Legends retail services.

                        By continuing you acknowledge that:
                        1. You use this software at your own risk on systems you control.
                        2. Online features connect only to servers you intentionally join.
                        3. There is no official matchmaking, account progression, or EA platform support in this path.
                        4. You will not use this software to attack, disrupt, or gain unauthorized access to systems you do not own or administer.
                        5. Game assets and trademarks remain the property of their respective owners.
                        """,
                    Version = 4,
                    Lang = vm.EulaUiLanguage(),
                };
                var gate = new Views.EulaWindow(null, NoticeClient.DefaultMasterServerUrl,
                    requireAccept: true, prefetched: prefetched, vm: vm);
                gate.Show();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Check("eula dialog: body shows the notice",
                    gate.FindControl<TextBox>("TxtBody")?.Text == prefetched.Contents);
                Check("eula dialog: Accept enabled when text is present",
                    gate.FindControl<Button>("BtnAccept")?.IsEnabled == true);
                Check("eula dialog: Accept/Decline offered for the gate",
                    gate.FindControl<Button>("BtnAccept")?.IsVisible == true
                    && gate.FindControl<Button>("BtnDecline")?.IsVisible == true
                    && gate.FindControl<Button>("BtnClose")?.IsVisible == false);
                var meta = gate.FindControl<TextBlock>("TxtMeta")?.Text;
                Check("eula dialog: meta shows the version", meta?.Contains("4") == true, meta ?? "");
                Check("eula dialog: not accepted until Accept is pressed", !gate.Accepted);

                // Paint it too: a dialog that only fails during layout (clipped
                // body, invisible buttons) shows up in the frame, not in the tree.
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                var eulaFrame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(gate);
                Check("eula dialog: frame rendered", eulaFrame is not null);
                if (eulaFrame is not null)
                {
                    var eulaPath = Path.Combine(Path.GetTempPath(), "r5flowstate-eula-selftest.png");
                    eulaFrame.Save(eulaPath);
                    var eulaSize = new FileInfo(eulaPath).Length;
                    Check("eula dialog: frame is not blank", eulaSize > 4 * 1024,
                        $"{eulaSize} bytes → {eulaPath}");
                }
                gate.Close();

                var readOnly = new Views.EulaWindow(null, NoticeClient.DefaultMasterServerUrl,
                    requireAccept: false, prefetched: prefetched, vm: vm);
                readOnly.Show();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Check("eula dialog: read-only hides Accept/Decline",
                    readOnly.FindControl<Button>("BtnAccept")?.IsVisible == false
                    && readOnly.FindControl<Button>("BtnDecline")?.IsVisible == false
                    && readOnly.FindControl<Button>("BtnClose")?.IsVisible == true);
                readOnly.Close();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
            catch (Exception ex)
            {
                Check("eula dialog pass", false, $"{ex.GetType().Name}: {ex.Message}");
            }

            // 6e. Download path: bytes are verified before anything is trusted.
            //     Runs against a loopback server, so the range/hash/atomic-move
            //     code is exercised for real without leaving the machine.
            DownloadSelfTest.Run(Check);

            // 6f. Install / update / verify / repair through the real content
            //     engine, against a synthetic channel in a temp folder: no
            //     network, nothing outside temp, and no EA install.
            InstallSelfTest.Run(Check);

            // 6g. Patch notes: the wire key, the bundled copies, the sibling
            //     lookup and the offline cache — the tab the old reader left
            //     permanently stuck on a placeholder. Reads the player's
            //     settings but never writes them.
            NotesSelfTest.Run(Check, vm);

            // 6h. Server browser: the master-server list the tab shows — the
            //     parser, the row shape, the ordering, the favourite identity,
            //     the address refusal, the lane flags and the tab gate. All
            //     fixtures: the only live probe is --servers, by design.
            BrowserSelfTest.Run(Check, vm);

            // 6i. Leaderboards: the master stats surface the tab reads — the
            //     parsers, the row and card derivation, the ordering rules, the
            //     season picker and the pager mapping. All fixtures: the only
            //     live probe is --leaderboard, by design.
            LeaderboardSelfTest.Run(Check, vm, window);

            // 6j. Blog: the site feed and the reader — the parsers, the slug
            //     gate, the row derivation, the unread stamp, the markdown
            //     grammar and its Avalonia rendering, down to a real click on a
            //     link inside a post. All fixtures: the only live probe is
            //     --blog, by design.
            BlogSelfTest.Run(Check, vm, window);

            // 6k. Console: the netcon wire and its id gate, the dedi output
            //     classifiers and the readiness rendezvous, and the tap — which
            //     is driven against throwaway /bin/sh children, because the
            //     line/command boundary is the tap's whole job and only a child
            //     that reads what we write can show it holds.
            ConsoleSelfTest.Run(Check, vm, window);

            // 6l. Playlist catalog: the install's own playlists, maps and names —
            //     the parser, the rtech hash behind every loc key, the curation
            //     allowlist, the map list on disk, and the view-model wiring that
            //     hands them to the combos. Fixtures in a temp folder, plus the
            //     real install's own def when there is one (read-only).
            CatalogSelfTest.Run(Check, vm);

            // 6m. The launcher's own desktop pieces: the install gate that has to
            //     tell a platform-only root from a game (the platform lane ships
            //     its own client.dll, which is the whole bug), the log file that
            //     outlives the window, how an EA install attempt reads from the
            //     outside, and the menu entries — all in temp folders with their
            //     roots redirected, so the real menu and log are never opened.
            DesktopSelfTest.Run(Check);

            // 6n. The Proton side: its prefix, the Wine inside that build (the only
            //     Wine this launcher may touch), the redistributable prep, the
            //     identity classifier, the loopback channel between the game and the
            //     EA App, and the loadscreen art cache. Wine is never executed and no
            //     prefix is ever bootstrapped — see EaSelfTest's own note on why.
            EaSelfTest.Run(Check);

            // 6b. Lay the window out and render one frame offscreen (Skia through
            //     the headless platform — nothing appears on screen). This is what
            //     catches template/style/binding failures that only surface during
            //     layout, and it proves the ported theme actually paints.
            try
            {
                window.Show();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Check("window laid out", window.Bounds.Width > 0 && window.Bounds.Height > 0,
                    $"{window.Bounds.Width}x{window.Bounds.Height}");

                var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                Check("frame rendered", frame is not null);
                if (frame is not null)
                {
                    var outPath = Path.Combine(Path.GetTempPath(), "r5flowstate-selftest.png");
                    frame.Save(outPath);
                    var size = new FileInfo(outPath).Length;
                    Check("frame is not blank", size > 8 * 1024, $"{size} bytes → {outPath}");

                    // Spot-check the palette: the top strip of the window is the R5F
                    // caption bar on WindowBg (#0B100D), so every channel must be very
                    // dark — a default light theme would show up immediately.
                    using var fb = frame.Lock();
                    var bytes = new byte[fb.RowBytes * fb.Size.Height];
                    System.Runtime.InteropServices.Marshal.Copy(fb.Address, bytes, 0, bytes.Length);
                    int off = 4 * fb.RowBytes + (fb.Size.Width / 2) * 4;
                    var b = bytes[off];
                    var g = bytes[off + 1];
                    var r = bytes[off + 2];
                    Check("theme painted WindowBg on the caption bar",
                        r < 40 && g < 40 && b < 40, $"rgb({r},{g},{b})");
                }
            }
            catch (Exception ex)
            {
                Check("render pass", false, $"{ex.GetType().Name}: {ex.Message}");
            }

            // 6e. The install tab's progress, wired end to end. Simple mode is what
            //     most players run, and its install tab had upstream's form without
            //     upstream's progress card: the bar was a literal Value="0" with a
            //     literal IsVisible="False", and nothing bound to either, so a
            //     download moved nothing on the tab that started it. Nothing here
            //     starts a job — the busy flag is set directly and put back, which is
            //     why the checks can be this specific. AttachInstallGate rides the
            //     same flag, and the window has already asked it once by now.
            var installCard = window.FindControl<Control>("PanelSimpleInstallProgress");
            var setupIdle = window.FindControl<Control>("PanelSimpleSetupIdle");
            var simpleBar = window.FindControl<ProgressBar>("BarSimpleInstall");
            var fileBar = window.FindControl<ProgressBar>("BarSimpleFile");
            var pauseButton = window.FindControl<Button>("BtnSimpleInstallPause");
            var cancelButton = window.FindControl<Button>("BtnSimpleInstallCancel");

            Check("the install card's buttons reach the job's own commands",
                pauseButton is not null && ReferenceEquals(pauseButton.Command, vm.TogglePauseInstallCommand)
                && cancelButton is not null && ReferenceEquals(cancelButton.Command, vm.CancelInstallCommand),
                pauseButton?.Command?.ToString() ?? "no command");

            var wasInstalling = vm.Installing;
            var setupCard = window.FindControl<Control>("PanelSimpleSetup");
            var playPanel = window.FindControl<Control>("PanelSimplePlay");
            var wasSetupShown = setupCard?.IsVisible ?? false;
            var wasPlayShown = playPanel?.IsVisible ?? false;
            try
            {
                vm.ClearInstallCard();
                vm.InstallFraction = 42;
                vm.InstallPaused = true;
                vm.Installing = true;

                // The card this block is about only exists while the setup card is
                // up, and that is decided from whether the game is installed on the
                // machine this runs on (MainWindow's AttachInstallGate). On a box
                // that has the game the whole card is hidden and would measure zero
                // high — so this stands the suite in the state a machine *without*
                // an install is in, exactly as the gate sets it, and puts it back
                // afterwards. After the flag, not before: the gate runs on the flag
                // and would overwrite anything set first.
                if (setupCard is not null) setupCard.IsVisible = true;
                if (playPanel is not null) playPanel.IsVisible = false;

                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                // The card has to lay out, not merely be visible: a panel that is
                // "shown" at zero height would be the same bug in a different dress,
                // and a missing style or resource would throw here rather than pass.
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Check("the install tab shows a progress card while a job runs",
                    installCard is { IsVisible: true } && installCard.Bounds.Height > 40,
                    installCard is null ? "no card" : $"height {installCard.Bounds.Height:0}");

                Check("the install tab's bar carries the job's fraction",
                    fileBar is not null && Math.Abs(fileBar.Value - 42) < 0.01,
                    fileBar?.Value.ToString("0.0") ?? "no bar");
                Check("the play tab's bar carries the same fraction",
                    simpleBar is not null && Math.Abs(simpleBar.Value - 42) < 0.01,
                    simpleBar?.Value.ToString("0.0") ?? "no bar");
                // Read off the TextBlocks, not off the view model: the card saying it
                // is the claim, and a correct property behind an unbound TextBlock is
                // exactly how this was first written (the busy frame still read
                // "Install the game" while the property said otherwise).
                var titleBlock = window.FindControl<TextBlock>("TxtSimpleSetupTitle");
                var kickerBlock = window.FindControl<TextBlock>("TxtSimpleSetupKicker");
                Check("the setup card reads Installing / hang tight while busy",
                    titleBlock?.Text == Loc.Get("installing_the_game")
                    && kickerBlock?.Text == Loc.Get("hang_tight"),
                    $"{kickerBlock?.Text} / {titleBlock?.Text}");
                Check("a held job offers Resume, not Pause",
                    vm.InstallPauseLabel == Loc.Get("resume")
                    && pauseButton?.Content?.ToString() == Loc.Get("resume"),
                    pauseButton?.Content?.ToString() ?? "no button");

                // And a download tick has to land in the card: rows that exist and
                // stay empty are the same complaint one step later. The relay is the
                // real one, fed a synthetic tick, so this covers the engine-to-card
                // wiring rather than a fixture's idea of it.
                new MainViewModel.InstallProgressRelay(vm).Report(new ContentInstallProgress
                {
                    Phase = "download",
                    Track = "Game files",
                    FileName = "cas/ab/9f2c1d",
                    Unit = ProgressUnit.Bytes,
                    Current = 2_000_000,
                    Total = 8_000_000,
                    StepIndex = 3,
                    StepCount = 5,
                    Active = new[]
                    {
                        new ActiveTransfer { Path = "cas/ab/9f2c1d", Done = 2_000_000, Total = 8_000_000 },
                    },
                });
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                // The tick's own fraction has to reach both bars before the frame is
                // taken, because that is the state the artifact claims to show.
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                var phaseText = window.FindControl<TextBlock>("TxtSimpleProgressPhase")?.Text;
                var fileText = window.FindControl<TextBlock>("TxtSimpleProgressFile")?.Text;
                var bytesText = window.FindControl<TextBlock>("TxtSimpleProgressBytes")?.Text;
                var stepText = window.FindControl<TextBlock>("TxtSimpleProgressStep")?.Text;
                Check("a download tick fills the card's phase and step rows",
                    phaseText == Loc.Get("phase_download")
                    && stepText == Loc.Format("part_of", 3, 5) + "  ·  Game files",
                    $"{phaseText} / {stepText}");
                Check("and its file and bytes rows",
                    fileText == "cas/ab/9f2c1d"
                    && bytesText is not null && bytesText.Contains(" / "),
                    $"{fileText} / {bytesText}");
                Check("the tick's in-flight file is listed with its own progress",
                    vm.InstallActive.Count == 1 && vm.InstallActive[0].Name == "9f2c1d"
                    && vm.InstallActive[0].Percent > 24 && vm.InstallActive[0].Percent < 26,
                    $"{vm.InstallActive.Count} row(s), {vm.InstallActive.FirstOrDefault()?.Percent:0.0}%");
                // 25% of the way through step 3 of 5 is 45% of the job. The bar has
                // to read the second number: reading the first would restart at every
                // step boundary, and this is the only place that can tell them apart.
                Check("the fraction the bar follows is the job's, not the step's",
                    Math.Abs(vm.InstallFraction - 45) < 0.01
                    && fileBar is not null && Math.Abs(fileBar.Value - 45) < 0.01,
                    $"vm {vm.InstallFraction:0.0}, bar {fileBar?.Value:0.0}");

                // The idle frame the render pass saves shows the folder form; this
                // one is what the player sees mid-download, so both states are on
                // disk to look at rather than only described.
                try
                {
                    var busyFrame = Avalonia.Headless.HeadlessWindowExtensions
                        .CaptureRenderedFrame(window);
                    if (busyFrame is not null)
                        busyFrame.Save(Path.Combine(Path.GetTempPath(),
                            "r5flowstate-selftest-installing.png"));
                }
                catch { /* the frame is an artifact, never a verdict */ }

                vm.InstallPaused = false;
                vm.Installing = false;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                Check("the card goes away when the job does, and the folder form returns",
                    installCard?.IsVisible == false && setupIdle?.IsVisible == true);
                Check("and the setup card reads Install the game again",
                    vm.InstallPauseLabel == Loc.Get("pause")
                    && titleBlock?.Text == Loc.Get("install_the_game")
                    && kickerBlock?.Text == Loc.Get("get_started"),
                    $"{kickerBlock?.Text} / {titleBlock?.Text}");
            }
            finally
            {
                // The panels first, so the busy flag's own handler (which re-derives
                // them from whether the game is installed) computes against the state
                // this run really is in rather than against the stand-in above.
                if (setupCard is not null) setupCard.IsVisible = wasSetupShown;
                if (playPanel is not null) playPanel.IsVisible = wasPlayShown;

                // The window is still shown from here on, so nothing may be left
                // looking busy — and InstallFraction is zeroed because a bar left at
                // 42 would be a lie told to the next check that looks at it.
                vm.InstallPaused = false;
                vm.Installing = wasInstalling;
                vm.ClearInstallCard();
            }

            // The in-flight list is the engine's snapshot in readable form. The
            // content store's paths are a shard and two hashes, which is not a thing
            // to read while waiting, so the row shows the file's own name (upstream
            // trims at the last slash the same way).
            vm.SetInstallTransfers(new[]
            {
                new ActiveTransfer { Path = "cas/ab/cd1f00", Done = 2_000_000, Total = 4_000_000 },
                new ActiveTransfer { Path = "C:\\x\\r5apex.exe", Done = 500 },
            });
            Check("in-flight files are listed by name, not by content-store path",
                vm.InstallActive.Count == 2
                && vm.InstallActive[0].Name == "cd1f00"
                && vm.InstallActive[1].Name == "r5apex.exe",
                string.Join(", ", vm.InstallActive.Select(r => r.Name)));
            Check("a transfer with no total reports what it has, not a division",
                vm.InstallActive[0].Detail.Contains(" / ")
                && !vm.InstallActive[1].Detail.Contains(" / ")
                && vm.InstallActive[1].Percent == 0,
                $"{vm.InstallActive[0].Detail} | {vm.InstallActive[1].Detail}");
            vm.ClearInstallCard();
            Check("clearing the card empties the list and the fraction",
                vm.InstallActive.Count == 0 && vm.InstallFraction == 0);

            // 7. First-run EA popup state is coherent: with no EA App in the
            //    prefix the popup must be raised (nothing is installed by the test).
            var eaPresent = vm.EaIsInstalled();
            Check("EA popup matches prefix state", vm.ShowEaDialog == !eaPresent,
                vm.ShowEaDialog ? "shown (no EA in prefix)" : "dismissed (EA present)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  selftest crashed: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            failures++;
        }
        finally
        {
            // Even a crashed run puts the file back: a suite that leaves the player's
            // settings changed when it dies is worse than one that never ran.
            RestoreSettingsBytes(settingsBefore);
        }

        Console.WriteLine(failures == 0
            ? "SELFTEST PASS — all checks green"
            : $"SELFTEST FAIL — {failures} failing check(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The player's settings file, read whole. Null when there is none — a first run
    /// has no baseline, and the restore below must not invent one.
    ///
    /// <para>The path is a parameter so the check that proves the net (<see
    /// cref="CheckSettingsNet"/>) can exercise it against a file of its own. A safety
    /// net that is only ever run against the thing it protects is a safety net nobody
    /// has seen work.</para>
    /// </summary>
    internal static byte[]? ReadSettingsBytes(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            // Unreadable is not a reason to fail every check that follows.
            return null;
        }
    }

    static byte[]? ReadSettingsBytes() => ReadSettingsBytes(LinuxSettings.ConfigPath);

    /// <summary>
    /// Put <paramref name="before"/> back when the run changed it, and say so once on
    /// stdout.
    ///
    /// <para>The note is not a PASS or a FAIL on purpose. A lane that writes the
    /// player's file is a real finding, but it is neither a broken product nor a
    /// broken check, and failing the run for it would make a green suite depend on
    /// whether the bundled changelog gained an entry since the last run — a test that
    /// goes red for a reason unrelated to what it tests gets muted, and then the
    /// finding is lost. A line that says what happened, and that the file was put
    /// back, is what keeps it visible without making it noise.</para>
    /// </summary>
    static void RestoreSettingsBytes(byte[]? before)
    {
        var outcome = RestoreSettingsBytes(before, LinuxSettings.ConfigPath, out var error);
        switch (outcome)
        {
            case SettingsRestore.Restored:
                Console.WriteLine($"NOTE  a lane wrote the player's settings; the bytes from "
                    + $"before this run started were put back ({LinuxSettings.ConfigPath})");
                break;

            case SettingsRestore.Failed:
                Console.WriteLine($"NOTE  the player's settings changed during this run and "
                    + $"could not be put back: {error} ({LinuxSettings.ConfigPath})");
                break;
        }
    }

    /// <summary>What a restore did. Reported by the caller rather than printed here,
    /// so the check below — which does restore a file, deliberately — does not put a
    /// line on stdout that reads like the real finding.</summary>
    internal enum SettingsRestore
    {
        /// <summary>Nothing was written, or nothing had been: the file is its own bytes.</summary>
        Unchanged,

        /// <summary>A lane's write was put back.</summary>
        Restored,

        /// <summary>It could not be — <c>error</c> says why.</summary>
        Failed,
    }

    /// <summary>
    /// The restore itself. Same temp-then-move the store itself uses, because a reader
    /// that catches this half-done would see a truncated settings file and answer with
    /// defaults.
    /// </summary>
    internal static SettingsRestore RestoreSettingsBytes(byte[]? before, string path, out string error)
    {
        error = string.Empty;
        if (before is null)
            return SettingsRestore.Unchanged;

        try
        {
            var now = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (now is not null && now.AsSpan().SequenceEqual(before))
                return SettingsRestore.Unchanged;

            var tmp = path + ".selftest";
            File.WriteAllBytes(tmp, before);
            File.Move(tmp, path, overwrite: true);
            return SettingsRestore.Restored;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return SettingsRestore.Failed;
        }
    }

    /// <summary>
    /// The net over the settings file, proved against a file of its own: a change is
    /// put back, an unchanged file is left alone, and a first run with no baseline
    /// invents nothing.
    ///
    /// <para>This is the check for the mechanism, not for the thing it protects. What
    /// it protects is checked where it matters — by hashing
    /// <c>~/.config/r5flowstate/settings.json</c> before and after a real run, which
    /// is what the docs record. What can be checked here is the half that would
    /// otherwise be taken on trust: that a changed file really is restored, and that
    /// the restore does not write when nothing changed (a rewrite would move the
    /// mtime of a file the suite has no business touching).</para>
    /// </summary>
    static void CheckSettingsNet(Action<string, bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "r5f-selftest-settings-" + Environment.ProcessId);
        var path = Path.Combine(dir, "settings.json");

        try
        {
            Directory.CreateDirectory(dir);

            // A first run: no file, no baseline, and nothing is written for it.
            var missing = ReadSettingsBytes(path);
            var invented = RestoreSettingsBytes(missing, path, out _) == SettingsRestore.Unchanged
                && !File.Exists(path);
            check("settings net: a run with no settings file yet writes none",
                missing is null && invented,
                invented ? "no baseline, no file" : "the restore created a file nobody had");

            var before = System.Text.Encoding.UTF8.GetBytes("{\"ClientWidth\": 0}");
            File.WriteAllBytes(path, before);

            // The leak in miniature: a lane saves something else, and the net puts the
            // bytes from before the run back.
            File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes("{\"ClientWidth\": 1920}"));
            var restored = RestoreSettingsBytes(before, path, out var error);
            var back = File.ReadAllBytes(path);
            check("settings net: a lane's write is put back, byte for byte",
                restored == SettingsRestore.Restored && back.AsSpan().SequenceEqual(before),
                restored == SettingsRestore.Restored
                    ? System.Text.Encoding.UTF8.GetString(back)
                    : $"{restored}: {error}");

            // And an unchanged file is not rewritten — the check that says the net is
            // not itself a writer.
            var stamp = File.GetLastWriteTimeUtc(path);
            Thread.Sleep(20);
            var quiet = RestoreSettingsBytes(before, path, out _) == SettingsRestore.Unchanged;
            check("settings net: a run that changed nothing writes nothing",
                quiet && File.GetLastWriteTimeUtc(path) == stamp,
                quiet ? $"mtime still {stamp:HH:mm:ss.fff}" : "the restore wrote anyway");
        }
        catch (Exception ex)
        {
            check("settings net: the file survives a run", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* a temp dir, and it is ours */ }
        }
    }

    /// <summary>
    /// Every loc key the shell names — <c>Loc.Get("…")</c>, <c>Loc.Format("…")</c> and
    /// <c>{loc:T …}</c> in XAML — has to exist in <c>english.json</c>. <see cref="Loc"/>
    /// returns the key itself when a lookup fails, so a missing key is not an error
    /// anywhere: it reaches the player as the raw key in the status bar. That is how
    /// `status_install_needed_to_play` was on screen while every other check was green.
    ///
    /// <para>Sources are read from the tree rather than from the assembly, so this only
    /// runs where the tree is (a repo checkout) and says so otherwise instead of passing
    /// by default.</para>
    /// </summary>
    /// <summary>
    /// BtnMapPicker's Click has to reach the picker's toggle. Checked against the
    /// source, not by raising the event (see the call site for why raising it would
    /// spawn a decoder), and checked as the pair it is: the XAML attribute and the
    /// method it names, so a rename on one side fails rather than silently
    /// disconnecting the button.
    /// </summary>
    static void CheckPickerHandler(Action<string, bool, string> check)
    {
        var shellDir = FindSourceDir();
        if (shellDir is null)
        {
            check("the map picker button's Click reaches its handler", true,
                "skipped — no source tree beside this build");
            return;
        }

        var xamlPath = Path.Combine(shellDir, "Views", "MainWindow.axaml");
        var codePath = Path.Combine(shellDir, "Views", "MainWindow.axaml.cs");
        if (!File.Exists(xamlPath) || !File.Exists(codePath))
        {
            check("the map picker button's Click reaches its handler", true,
                "skipped — no MainWindow sources beside this build");
            return;
        }

        // The element itself, attributes and content, so the attribute is read on
        // BtnMapPicker and not on whichever button happens to follow it.
        var element = System.Text.RegularExpressions.Regex.Match(
            StripComments(File.ReadAllText(xamlPath)),
            "<Button\\b[^>]*x:Name=\"BtnMapPicker\"[\\s\\S]*?</Button>");
        var wired = element.Success
                    && element.Value.Contains("Click=\"OnToggleMapPicker\"", StringComparison.Ordinal);
        var handler = File.ReadAllText(codePath)
            .Contains("void OnToggleMapPicker(", StringComparison.Ordinal);

        check("the map picker button's Click reaches its handler", wired && handler,
            !element.Success ? "BtnMapPicker not found in MainWindow.axaml"
            : !wired ? "it carries no Click=\"OnToggleMapPicker\""
            : !handler ? "OnToggleMapPicker is not in the code-behind"
            : "Click=\"OnToggleMapPicker\" and the method both present");
    }

    /// <summary>
    /// The hero lane's own bookkeeping, driven through the view model with the
    /// decoder stood in for (<see cref="MainViewModel.BatchOverride"/>): which
    /// requests reach the decoder, how many, which of them the launcher walks away
    /// from, and what the log says about a batch that was replaced before it could
    /// answer.
    ///
    /// <para>This is the regression the player reported. The port was missing
    /// Windows' <c>_inflightLoadscreenKey</c>, so every chrome pass re-queued the
    /// hero and each one cancelled the decode the pass before it had started — and
    /// because the art host is killed before it writes a single record, the caller
    /// read its own cancellation as a decoder that had failed. Sixty of the player's
    /// "the art host did not report this one" lines, every one of them preceded by
    /// the launcher's own cancellation line for the same batch. Four things are
    /// asserted here: the guard (a repeat request is not a request), the honest
    /// wording for a batch that really is replaced, the guard that a picture already
    /// on screen is not asked for again, and the rule that a batch which failed for a
    /// real reason still gets its line — without which "no line" would prove nothing.</para>
    ///
    /// <para>Hermetic. The seam sits above the Proton and install gate, so no Proton
    /// build, no host and no install is needed and nothing is spawned. The install
    /// root is a temp tree holding one 5 KB <c>loadscreen_*.rpak</c>, the decode cache
    /// is <see cref="ArtDecode.CacheRootEnvVar"/> pointed at a temp folder, the log is
    /// a temp file, and the two map stems are this check's own — so every line it
    /// reads can only be one it caused.</para>
    /// </summary>
    static void CheckLoadscreenQueue(MainViewModel vm, Action<string, bool, string> check)
    {
        static bool IsLobby(string? stem) =>
            !string.IsNullOrWhiteSpace(stem) && stem.Contains("lobby", StringComparison.OrdinalIgnoreCase);

        static void Pump(Func<bool> until, int ms)
        {
            var deadline = Environment.TickCount64 + ms;
            while (!until() && Environment.TickCount64 < deadline)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        static List<string> Lines(string path)
        {
            try { return File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>(); }
            catch { return new List<string>(); }
        }

        var stamp = Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "r5f-artq-" + stamp);
        var logPath = Path.Combine(root, "launcher.log");

        var installBefore = vm.InstallPath;
        var selectedBefore = vm.SelectedMode;
        var logBefore = LauncherLog.LogPath;
        var cacheBefore = Environment.GetEnvironmentVariable(ArtDecode.CacheRootEnvVar);

        // A card of this check's own, so both stems are unique to it: the log
        // assertions below are exact because no other lane can name them.
        var stemA = "mp_rr_selftest_" + stamp;
        var stemB = stemA + "_b";
        var card = new ModeCardViewModel("selftest_family_" + stamp, ModeGroups.Apex,
            "Selftest", "fixture", new[]
            {
                new MapOption(stemA, "Selftest A", "selftest_playlist_a"),
                new MapOption(stemB, "Selftest B", "selftest_playlist_b"),
            }, new MapOption(stemA, "Selftest A", "selftest_playlist_a"));

        var requests = new List<(ArtRequest Req, CancellationToken Token, TaskCompletionSource<ArtBatch> Answer)>();
        var gate = new object();
        var empty = new ArtBatch(Array.Empty<ArtOutcome>(), false, 0, string.Empty, string.Empty);

        void Answer(int i, ArtBatch batch) => requests[i].Answer.TrySetResult(batch);
        string Describe(int i) => requests[i].Req.Stems is { Count: > 0 } s
            ? string.Join("+", s)
            : string.Join("+", requests[i].Req.Paks ?? new List<string>());

        try
        {
            var pakDir = Path.Combine(root, "paks", "Win64");
            Directory.CreateDirectory(pakDir);
            var fakePak = Path.Combine(pakDir, "loadscreen_selftest_x.rpak");
            File.WriteAllBytes(fakePak, new byte[5120]);

            Environment.SetEnvironmentVariable(ArtDecode.CacheRootEnvVar, Path.Combine(root, "cache"));
            LauncherLog.LogPath = logPath;

            // The seam stands in for one run of the hosted decoder. Run
            // continuations asynchronously so a request is only ever recorded on
            // this thread, and the lane's own continuations land where they do in
            // the launcher: on the pool, posting their state changes back here.
            MainViewModel.BatchOverride = (req, token) =>
            {
                var tcs = new TaskCompletionSource<ArtBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (gate) requests.Add((req, token, tcs));
                return tcs.Task;
            };

            // A state to measure from, reached through the lane's own API and
            // without a request: a map-stem card selected with no install named
            // clears the shown pak, cancels the hero, drops the in-flight key and
            // shows nothing. Nothing may depend on what the window's own
            // construction left behind — the view model's RebuildModeCards ends at
            // RefreshHero, so this lane has already run once by now.
            vm.InstallPath = string.Empty;
            vm.SelectModeCard(card, persist: false);
            vm.QueueLoadscreen();
            check("the hero lane starts each picture from a known state",
                requests.Count == 0 && vm.HeroArt is null,
                $"{requests.Count} request(s) queued while nothing was selected");

            vm.InstallPath = root;

            // ---------------------------------------------------------- the lobby
            //
            // The lobby ships a stub loadscreen and borrows the standalone art
            // set, so its hero is one random art pak -- Windows' QueueArtLoadscreen.
            var lobby = vm.ModeCards.FirstOrDefault(c => ModeCardViewModel.IsLobbyPlaylist(c.Id)
                || (c.MapIsPinned && IsLobby(c.SelectedMapStem)));

            if (lobby is null)
            {
                check("the lobby's hero asks the decoder once for one art pak", true,
                    "skipped — this machine's catalog has no lobby card");
            }
            else
            {
                vm.SelectModeCard(lobby, persist: false);
                check("the lobby's hero asks the decoder once for one art pak",
                    requests.Count == 1 && !requests[0].Token.IsCancellationRequested
                    && requests[0].Req.Stems is null && requests[0].Req.Paks is { Count: 1 }
                    && requests[0].Req.Width == LoadscreenArt.HeroWidth,
                    $"{requests.Count} request(s): {Describe(0)}");

                // The chrome pass is what the player's log was full of: three
                // refreshes in one second, each one previously a fresh decode.
                vm.RefreshHero();
                vm.QueueLoadscreen();
                vm.QueueLoadscreen();
                check("a chrome pass cannot supersede the lobby decode it is waiting on",
                    requests.Count == 1 && !requests[0].Token.IsCancellationRequested,
                    requests.Count == 1
                        ? "still one request, and its token is live"
                        : $"{requests.Count} requests — each pass re-queued the hero");

                // Half of Windows' guard is "the art is already up"; the other
                // half, the one that was missing, is "something is being decoded
                // right now". With no picture shown yet, only that half can be
                // what suppresses the three calls above.
                Answer(0, empty);

                // ...and the key is let go as the decode returns, so the same
                // picture can be asked for again. Pumped through this call, which
                // is a no-op for exactly as long as the key is held.
                Pump(() =>
                {
                    vm.QueueLoadscreen();
                    return requests.Count > 1;
                }, 3000);
                check("the in-flight key is released as the decode returns",
                    requests.Count == 2, $"{requests.Count} request(s)");

                // The positive control for the suppression rule below: a batch
                // that failed for the decoder's own reason still gets its line.
                if (requests.Count == 2)
                {
                    var reason = "loadscreen_selftest_x.rpak: empty pak";
                    Answer(1, new ArtBatch(new[] { ArtOutcome.Missing("pak", fakePak, reason) },
                        false, -1, string.Empty, "records.tsv"));
                    Pump(() => Lines(logPath).Any(l => l.Contains("Loadscreen pak " + fakePak
                        + ": " + reason, StringComparison.Ordinal)), 3000);

                    var told = Lines(logPath).Count(l => l.Contains("Loadscreen pak " + fakePak
                        + ": " + reason, StringComparison.Ordinal));
                    check("a picture the decoder refused is reported by name",
                        told == 1, $"{told} line(s) about {Path.GetFileName(fakePak)}");
                }
            }

            // ------------------------------------------------------------ the map
            requests.Clear();
            vm.SelectModeCard(card, persist: false);
            check("a map's hero asks the decoder once for that map",
                requests.Count == 1 && !requests[0].Token.IsCancellationRequested
                && requests[0].Req.Stems is { Count: 1 } one && one[0] == stemA,
                $"{requests.Count} request(s): {Describe(0)}");

            vm.RefreshHero();
            vm.QueueLoadscreen();
            vm.QueueLoadscreen();
            check("a chrome pass cannot supersede the map decode it is waiting on",
                requests.Count == 1 && !requests[0].Token.IsCancellationRequested,
                requests.Count == 1
                    ? "still one request, and its token is live"
                    : $"{requests.Count} requests — each pass re-queued the hero");

            // The tile click: the mode keeps the map you picked, and the hero
            // follows (Windows' OnMapTileClick).
            card.SelectedMap = card.Maps[1];
            vm.QueueLoadscreen();
            check("picking another map supersedes the decode for the old one",
                requests.Count == 2 && requests[0].Token.IsCancellationRequested
                && !requests[1].Token.IsCancellationRequested
                && requests[1].Req.Stems is { Count: 1 } next && next[0] == stemB,
                $"{requests.Count} request(s): {Describe(0)} then {Describe(1)}");

            // The batch nobody is waiting for any more: it did reach the host, the
            // host was killed mid-decode, and its records file is still empty. The
            // honest answer is the replacement, said once -- not the host's silence
            // said once per picture, which is what the player's log was full of.
            var replaced = new ArtBatch(
                new[] { ArtOutcome.Missing("stem", stemA, ArtDecode.ReplacedReason) },
                false, -1, ArtDecode.ReplacedNote, "records.tsv");
            Answer(0, replaced);
            Pump(() => Lines(logPath).Any(l => l.EndsWith("Loadscreen art: " + ArtDecode.ReplacedNote,
                StringComparison.Ordinal)), 3000);

            var lines = Lines(logPath);
            check("a replaced batch is reported as replaced, and once",
                lines.Any(l => l.EndsWith("Loadscreen art: " + ArtDecode.ReplacedNote, StringComparison.Ordinal))
                && lines.All(l => !l.Contains(ArtDecode.UnreportedReason, StringComparison.Ordinal))
                && lines.All(l => !l.Contains(stemA, StringComparison.Ordinal)),
                lines.Count(l => l.Contains(ArtDecode.ReplacedNote, StringComparison.Ordinal)) + " note line(s), "
                + lines.Count(l => l.Contains(ArtDecode.UnreportedReason, StringComparison.Ordinal)) + " silence, "
                + lines.Count(l => l.Contains(stemA, StringComparison.Ordinal)) + " naming the superseded map");

            // What the decoder did report is what the pane shows: a real R5FA file
            // the host would have written, read back through the cache and turned
            // into a bitmap. This is also the one place the suite exercises the
            // pixels-to-Bitmap half of LoadscreenArt.
            var cacheB = ArtDecode.CacheFileForStem(LoadscreenArt.HeroWidth, stemB);
            var pixels = new LoadscreenPixels
            {
                Width = 8,
                Height = 8,
                Bgra = new byte[8 * 8 * 4],
                SourcePath = "selftest fixture",
            };
            for (var i = 0; i < pixels.Bgra.Length; i += 4)
            {
                pixels.Bgra[i] = 40;      // B
                pixels.Bgra[i + 1] = 90;  // G
                pixels.Bgra[i + 2] = 200; // R
                pixels.Bgra[i + 3] = 255; // A
            }
            ArtCache.Write(cacheB, pixels, out var writeError);

            Answer(1, new ArtBatch(
                new[] { new ArtOutcome("stem", stemB, cacheB, true, string.Empty, "selftest fixture") },
                true, 0, string.Empty, "records.tsv"));
            Pump(() => vm.HeroArt is not null, 5000);
            check("the picture the decoder reported is the one the hero pane shows",
                vm.HeroArt is Avalonia.Media.Imaging.Bitmap bmp
                && bmp.PixelSize.Width == 8 && bmp.PixelSize.Height == 8,
                vm.HeroArt is Avalonia.Media.Imaging.Bitmap b
                    ? $"{b.PixelSize.Width}x{b.PixelSize.Height} from {cacheB}"
                    : $"{vm.HeroArt?.GetType().Name ?? "no art"} ({writeError ?? "cache file written"})");

            // Windows' other guard: a picture already on screen is not asked for
            // again. The in-flight key is long released by now, so this half is the
            // one doing the work.
            vm.QueueLoadscreen();
            vm.RefreshHero();
            vm.QueueLoadscreen();
            check("a picture already on screen is not asked for again",
                requests.Count == 2, $"{requests.Count} request(s) for 2 pictures");
        }
        catch (Exception ex)
        {
            check("the hero lane's queue", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Order matters. The install path is cleared first so the selection
            // below queues nothing at all, and the seam is still set while it goes
            // back so the restore cannot reach Proton either; only then do the real
            // install path, the cache root and the log come back.
            try
            {
                vm.InstallPath = string.Empty;
                vm.SelectModeCard(selectedBefore ?? card, persist: false);
            }
            catch { /* a restore that throws must not hide the checks above */ }

            MainViewModel.BatchOverride = null;
            vm.InstallPath = installBefore;
            Environment.SetEnvironmentVariable(ArtDecode.CacheRootEnvVar, cacheBefore);
            LauncherLog.LogPath = logBefore;

            try { Directory.Delete(root, recursive: true); } catch { /* temp debris is not a failure */ }
        }
    }

    /// <summary>
    /// The Res row's wiring, read from the source like the picker's Click: two
    /// buttons have to reach <c>OnResolutionPresetClick</c> and four boxes have to
    /// commit on <c>OnClientResolutionLostFocus</c>. The suite already covers what
    /// those handlers do (<c>DisplayModesChecks</c> builds the very launch lines they
    /// produce); this is the other half — that the controls on screen are the ones
    /// calling them.
    /// </summary>
    static void CheckResolutionWiring(Action<string, bool, string> check)
    {
        var shellDir = FindSourceDir();
        if (shellDir is null)
        {
            check("the Res buttons and the dimension boxes are wired", true,
                "skipped — no source tree beside this build");
            return;
        }

        var xaml = StripComments(File.ReadAllText(
            Path.Combine(shellDir, "Views", "MainWindow.axaml")));
        var buttons = System.Text.RegularExpressions.Regex.Matches(xaml,
            "<Button\\b[^>]*(x:Name=\"(?:BtnResolutionPreset|BtnServersResolution)\")[^>]*>");
        var wiredButtons = buttons.Count(m => m.Value.Contains("Click=\"OnResolutionPresetClick\"",
            StringComparison.Ordinal));
        var boxes = System.Text.RegularExpressions.Regex.Matches(xaml,
            "<TextBox\\b[^>]*LostFocus=\"OnClientResolutionLostFocus\"[^>]*>");

        check("the Res buttons and the dimension boxes are wired",
            buttons.Count == 2 && wiredButtons == 2 && boxes.Count == 4,
            $"Res buttons {wiredButtons}/{buttons.Count} open the menu, "
            + $"{boxes.Count}/4 dimension boxes commit on LostFocus");
    }

    /// <summary>
    /// The relay's half of the console contract, pinned against this half by reading
    /// both sources: the four environment names, the handshake word, the pipe paths
    /// never being spelled on this side, and the relay really being upstream's pipe
    /// code behind a build of its own.
    ///
    /// <para>Source reads rather than references, because there is no reference to
    /// have: <c>R5Flowstate.ConsoleRelay</c> is <c>net8.0-windows</c> and
    /// <c>R5Flowstate.Linux.Core</c> is not, so neither can compile against the other
    /// — which is exactly how a name drifts. A drifted name reads to the player as a
    /// game whose console is silently empty, the bug this whole lane exists to
    /// remove, so it fails here instead.</para>
    /// </summary>
    static void CheckConsoleContract(Action<string, bool, string> check)
    {
        var shellDir = FindSourceDir();
        var repo = shellDir is null ? null : Path.GetFullPath(Path.Combine(shellDir, "..", ".."));
        var upstreamPath = repo is null ? null
            : Path.Combine(repo, "src", "R5Flowstate.Spawn", "HostedConsoleTap.cs");
        var relayPath = repo is null ? null
            : Path.Combine(repo, "src", "R5Flowstate.ConsoleRelay", "Relay.cs");
        var relayProject = repo is null ? null
            : Path.Combine(repo, "src", "R5Flowstate.ConsoleRelay", "R5Flowstate.ConsoleRelay.csproj");
        var linuxPath = repo is null ? null
            : Path.Combine(repo, "src", "R5Flowstate.Linux.Core", "HostedConsole.cs");

        if (upstreamPath is null || !File.Exists(upstreamPath) || !File.Exists(relayPath)
            || !File.Exists(relayProject) || !File.Exists(linuxPath))
        {
            check("the console contract matches upstream's", true,
                "skipped — no source tree beside this build");
            return;
        }

        // A `const string X = "…"` in a file that is not this one.
        static string? Literal(string source, string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(source,
                "const\\s+string\\s+" + name + "\\s*=\\s*\"([^\"]*)\"");
            return match.Success ? match.Groups[1].Value : null;
        }

        var upstream = StripComments(File.ReadAllText(upstreamPath));
        var names = new (string Name, string Ours)[]
        {
            ("HostedEnv", HostedConsole.HostedEnv),
            ("PipeEnv", HostedConsole.PipeEnv),
            ("InEnv", HostedConsole.InEnv),
            ("RoleEnv", HostedConsole.RoleEnv),
        };
        var drifted = names
            .Where(n => Literal(upstream, n.Name) != n.Ours)
            .Select(n => $"{n.Name}: upstream '{Literal(upstream, n.Name) ?? "(not a const)"}' "
                + $"vs ours '{n.Ours}'")
            .ToList();
        check("the four console names are the ones the game's own console setup reads",
            drifted.Count == 0,
            drifted.Count == 0 ? string.Join(", ", names.Select(n => n.Ours))
                : string.Join("; ", drifted));

        // The handshake's word is the relay's, and this side reads it as its own.
        var relay = StripComments(File.ReadAllText(relayPath));
        check("the relay's handshake word is the one this side waits for",
            Literal(relay, "HelloTag") == HostedConsole.HelloTag,
            $"relay '{Literal(relay, "HelloTag") ?? "(not a const)"}' vs ours '{HostedConsole.HelloTag}'");

        // The pipe paths come off the wire: upstream spells `\\.\pipe\` because it
        // creates them; this side must not, or it would be reconstructing names it
        // has no way to keep in step with the ones that exist.
        var prefix = "\\\\.\\pipe\\";
        var upstreamSpells = upstream.Contains(prefix, StringComparison.Ordinal);
        var oursSpells = StripComments(File.ReadAllText(linuxPath)).Contains(prefix, StringComparison.Ordinal);
        check("the pipe paths are read off the relay, never reconstructed on this side",
            upstreamSpells && !oursSpells,
            upstreamSpells
                ? (oursSpells
                    ? "this side spells a pipe path too"
                    : "upstream creates them; this side only reads them")
                : "upstream no longer spells a pipe path — is it still creating the pipes?");

        // And the relay is a build of its own around upstream's tap rather than a
        // copy of it: the project reference is the reason the game-facing half
        // cannot drift, and the name has to be the one this side looks for.
        var project = File.ReadAllText(relayProject);
        var referencesUpstream = project.Contains("R5Flowstate.Spawn.csproj", StringComparison.Ordinal);
        var usesUpstream = relay.Contains("HostedConsoleTap.Create(", StringComparison.Ordinal);
        check("the relay is a build around upstream's tap, named the one this side looks for",
            referencesUpstream && usesUpstream
            && project.Contains("<AssemblyName>r5f-relay</AssemblyName>", StringComparison.Ordinal)
            && project.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", StringComparison.Ordinal)
            && HostedConsole.RelayExeName == "r5f-relay.exe",
            !referencesUpstream ? "the relay project no longer references R5Flowstate.Spawn"
            : !usesUpstream ? "the relay no longer calls HostedConsoleTap.Create"
            : $"{HostedConsole.RelayExeName}, win-x64, on upstream's tap");
    }

    /// <summary>
    /// The console tab's three controls that were bound to the wrong answer, and
    /// the one place where the port had to be Avalonia rather than WPF.
    ///
    /// <para>All three were <em>present</em>, which is why the surface check said
    /// nothing: the player saw a gamemode list rendering <c>R5Flowstate.Shell…</c>,
    /// a Stop that killed the dedicated server and left the game running, and a
    /// Clear that cleared one pane of two.</para>
    ///
    /// <para><b>Why ToString and not DisplayMemberPath.</b> Upstream sets
    /// <c>DisplayMemberPath="Title"</c> and <c>"DisplayName"</c> on these two combos,
    /// and that property does not exist in Avalonia 11 — the XAML compile refuses it
    /// (AVLN2000), which is how it was found. Avalonia's equivalent for an
    /// untemplated item is the item's own <c>ToString()</c>, and
    /// <see cref="MapOption"/> has overridden it for <c>DisplayName</c> since the
    /// port was written. So the check is on the objects themselves: every card and
    /// every map has to say what the combo would show, and it must not be the type
    /// name.</para>
    /// </summary>
    static void CheckStopWiring(Action<string, bool, string> check, MainViewModel vm, Window window)
    {
        var action = window.FindControl<Button>("BtnConsoleAction");
        var clear = window.FindControl<Button>("BtnConsoleClear");
        check("the console tab's STOP is the session's stop, not the dedi's own kill",
            action is not null
            && ReferenceEquals(action.Command, vm.StopSessionCommand)
            && !ReferenceEquals(action.Command, vm.KillDediCommand),
            action?.Command?.ToString() ?? "no command");

        check("the console tab's CLEAR clears both logs, not the dedi's pane alone",
            clear is not null
            && ReferenceEquals(clear.Command, vm.ClearConsoleBothCommand)
            && !ReferenceEquals(clear.Command, vm.ClearConsoleServerCommand),
            clear?.Command?.ToString() ?? "no command");

        // What a combo with no item template shows. A card whose ToString is its own
        // type name is a card the player cannot read, which is the report verbatim.
        var cards = vm.ModeCards.ToList();
        var cardNames = cards.Take(3).Select(x => x.ToString()).ToList();
        check("a mode card shows its title where a combo asks it to render itself",
            cards.Count > 0
            && cards.All(x => string.Equals(x.ToString(), x.Title, StringComparison.Ordinal)
                && !x.ToString().Contains("ViewModel", StringComparison.Ordinal)),
            cards.Count == 0 ? "no cards loaded" : string.Join(" | ", cardNames));

        var maps = cards.SelectMany(x => x.Maps).ToList();
        check("a map shows its display name, which is what the map combo needed",
            maps.Count > 0
            && maps.All(m => string.Equals(m.ToString(), m.DisplayName, StringComparison.Ordinal)),
            maps.Count == 0 ? "no maps loaded" : string.Join(" | ", maps.Take(3).Select(m => m.ToString())));
    }

    /// <summary>
    /// No console window, in both the projects that decide it and the code that had
    /// to stop depending on having one.
    ///
    /// <para>Windows allocates a console from the PE subsystem and nothing else, and
    /// Proton keeps the rule — so a helper built as a console binary (<c>Exe</c>,
    /// subsystem 3) gets a dark Wine console on screen every time it starts. The
    /// player saw one for every art batch and one per relay per launch, which is what
    /// "the dark separate console appears" was. <c>WinExe</c> is subsystem 2, and
    /// <see cref="EaSelfTest"/>'s <c>WinHostChecks</c> reads the field out of the
    /// installed binaries; this is the projects' half, plus the two source shapes
    /// that become load-bearing the moment the binaries stop having a console:
    /// the art host's records file has to be written whether or not there is
    /// anywhere to print, and the relay's own trace has to be unable to throw.</para>
    /// </summary>
    static void CheckHelperSubsystems(Action<string, bool, string> check)
    {
        var shellDir = FindSourceDir();
        var repo = shellDir is null ? null : Path.GetFullPath(Path.Combine(shellDir, "..", ".."));

        string? PathOf(params string[] parts) =>
            repo is null ? null : Path.Combine(new[] { repo }.Concat(parts).ToArray());

        var artProject = PathOf("src", "R5Flowstate.ArtHost", "R5Flowstate.ArtHost.csproj");
        var relayProject = PathOf("src", "R5Flowstate.ConsoleRelay", "R5Flowstate.ConsoleRelay.csproj");
        var artProgram = PathOf("src", "R5Flowstate.ArtHost", "Program.cs");
        var relaySource = PathOf("src", "R5Flowstate.ConsoleRelay", "Relay.cs");

        if (artProject is null || relayProject is null || artProgram is null || relaySource is null
            || !File.Exists(artProject) || !File.Exists(relayProject)
            || !File.Exists(artProgram) || !File.Exists(relaySource))
        {
            check("both Windows helpers are built as GUI binaries", true,
                "skipped — no source tree beside this build");
            return;
        }

        static string OutputType(string csproj) =>
            System.Text.RegularExpressions.Regex.Match(csproj, "<OutputType>([^<]*)</OutputType>")
                .Groups[1].Value;

        var art = OutputType(File.ReadAllText(artProject));
        var relay = OutputType(File.ReadAllText(relayProject));
        check("both Windows helpers are built as GUI binaries, so no console window is allocated",
            art == "WinExe" && relay == "WinExe",
            $"art host '{art}', relay '{relay}' — WinExe is PE subsystem 2");

        // `static void Record(...)` up to the method's own closing brace.
        static string BodyOf(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0)
                return string.Empty;
            var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
            return end < 0 ? string.Empty : source[start..end];
        }

        var program = StripComments(File.ReadAllText(artProgram));
        var record = BodyOf(program, "static void Record(");
        check("the art host's record reaches the file even when there is no console to print it to",
            record.Contains("records?.Write(line);", StringComparison.Ordinal)
            && record.Contains("Console.WriteLine(line)", StringComparison.Ordinal)
            && record.IndexOf("records?.Write(line);", StringComparison.Ordinal)
                < record.IndexOf("Console.WriteLine(line)", StringComparison.Ordinal)
            && record.Contains("catch", StringComparison.Ordinal),
            record.Length == 0
                ? "Record was not found — is it still a method of its own?"
                : "the file is written first, and the stdout write is inside a try");

        var say = BodyOf(StripComments(File.ReadAllText(relaySource)), "public static void Say(");
        check("the relay's own trace cannot throw when it has no console to write to",
            say.Contains("catch", StringComparison.Ordinal),
            say.Length == 0 ? "Say was not found — is it still a method of its own?"
                : "guarded");
    }

    /// <summary>
    /// install.sh's side of the winhost layout, read from the script.
    ///
    /// <para>Read from install.sh rather than only from <see cref="WinHost"/> because
    /// install.sh is where the mistake was made: the locator can only look in the
    /// folders it is told about, and for a while it was told two helpers shared one.
    /// The two publishes are the thing that has to differ, so the two publishes are
    /// what this reads — the destination of each <c>dotnet publish</c> and the exact
    /// folder <see cref="WinHost.FolderFor"/> derives from the exe name.</para>
    ///
    /// <para>Hermetic: a text read of a script beside this build, no seam and no
    /// execution. An installed launcher has no <c>install.sh</c> above it and reports
    /// the skip.</para>
    /// </summary>
    static void CheckInstallLayout(Action<string, bool, string> check)
    {
        var shellDir = FindSourceDir();
        var repo = shellDir is null ? null : Path.GetFullPath(Path.Combine(shellDir, "..", ".."));
        var scriptPath = repo is null ? null : Path.Combine(repo, "install.sh");

        if (scriptPath is null || !File.Exists(scriptPath))
        {
            check("install.sh gives each Windows helper a folder of its own", true,
                "skipped — no source tree beside this build");
            return;
        }

        var script = StripComments(File.ReadAllText(scriptPath));

        // `dotnet publish "$VAR" -c Release -o "<somewhere>"`, one per project. The
        // launcher is published the same way, so the helpers are the ones landing
        // under winhost/ — which is itself the property being checked.
        var publish = new System.Text.RegularExpressions.Regex(
            "dotnet\\s+publish\\s+\"\\$([A-Za-z0-9_]+)\"[^\\n]*?-o\\s+\"([^\"]+)\"");
        var helpers = new System.Collections.Generic.List<(string Project, string Out)>();
        foreach (System.Text.RegularExpressions.Match m in publish.Matches(script))
        {
            var target = m.Groups[2].Value.TrimEnd('/');
            if (target.Contains("/" + WinHost.FolderName + "/", StringComparison.Ordinal))
                helpers.Add((m.Groups[1].Value, target));
        }

        var wanted = new[]
        {
            WinHost.FolderFor(ArtDecode.HostExeName),
            WinHost.FolderFor(HostedConsole.RelayExeName),
        };
        var folders = helpers.Select(h => Path.GetFileName(h.Out)).ToList();
        check("install.sh publishes each Windows helper into a folder of its own, named the way the locator looks",
            helpers.Count == 2
            && folders.Distinct(StringComparer.Ordinal).Count() == 2
            && folders.All(f => wanted.Contains(f, StringComparer.Ordinal))
            && wanted.All(w => folders.Contains(w, StringComparer.Ordinal)),
            helpers.Count == 0 ? "nothing publishes under winhost/ in install.sh"
                : string.Join(", ", helpers.Select(h => $"${h.Project} -> {h.Out}")));

        // oo2core sits beside the exe, because that is the only place the decoder
        // loads it from — inside the prefix that folder IS AppContext.BaseDirectory.
        // A copy left flat under winhost/ is a DLL with nothing to load it.
        var hostFolder = WinHost.FolderFor(ArtDecode.HostExeName);
        var beside = System.Text.RegularExpressions.Regex.Match(script,
            "\\[\\s*!\\s*-f\\s+\"([^\"]*oo2core[^\"]*)\"");
        var besideFolder = beside.Success
            ? Path.GetFileName(Path.GetDirectoryName(beside.Groups[1].Value) ?? string.Empty)
            : null;

        check("install.sh looks for oo2core in the art host's own folder",
            beside.Success && besideFolder == hostFolder
            && script.Contains($"{WinHost.FolderName}/{hostFolder}/", StringComparison.Ordinal),
            beside.Success
                ? $"tested at '{beside.Groups[1].Value}' ({besideFolder ?? "no folder"})"
                : "install.sh no longer tests for oo2core beside the host");
    }

    static void CheckLocKeys(Action<string, bool, string> check)
    {
        var shellDir = FindSourceDir();
        if (shellDir is null)
        {
            check("loc keys: every key the shell names resolves", true,
                "skipped — no source tree beside this build");
            return;
        }

        var missing = new System.Collections.Generic.List<string>();
        var known = LoadKeys(Path.Combine(shellDir, "Loc", "english.json"));

        // Two shapes: a C# lookup with a literal, and a XAML extension.
        var pattern = new System.Text.RegularExpressions.Regex(
            "Loc\\.(?:Get|Format)\\(\\s*\"([^\"]+)\"|\\{loc:T ([A-Za-z0-9_]+)\\}");
        foreach (var file in Directory.EnumerateFiles(shellDir, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal)
                                  || f.EndsWith(".axaml", StringComparison.Ordinal))
                                 && !f.Contains("/obj/") && !f.Contains("/bin/")))
        {
            foreach (System.Text.RegularExpressions.Match m in pattern.Matches(StripComments(File.ReadAllText(file))))
            {
                var key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (key.Length > 0 && !known.ContainsKey(key)
                    && !missing.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    missing.Add(key);
                }
            }
        }

        check("loc keys: every key the shell names resolves",
            missing.Count == 0,
            missing.Count == 0
                ? $"{known.Count} keys in english.json"
                : string.Join(", ", missing));
    }

    /// <summary>Drop whole-line comments before scanning. A doc comment describing
    /// this check — <c>Loc.Get("…")</c>, <c>{loc:T …}</c> — is not a key the shell
    /// names, and counting it as one made this check fail on itself.</summary>
    static string StripComments(string text)
    {
        var kept = new System.Collections.Generic.List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal)
                || trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                continue;
            }
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    static System.Collections.Generic.Dictionary<string, string> LoadKeys(string path)
    {
        var table = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        foreach (var prop in doc.RootElement.EnumerateObject())
            table[prop.Name] = prop.Value.GetString() ?? "";
        return table;
    }

    /// <summary>The shell's source directory, walking up from this assembly. Null when
    /// the build sits somewhere without one (the installed tree).</summary>
    static string? FindSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "R5Flowstate.Shell.Linux", "Loc",
                "english.json");
            if (File.Exists(candidate))
                return Path.Combine(dir.FullName, "src", "R5Flowstate.Shell.Linux");
        }
        return null;
    }
}