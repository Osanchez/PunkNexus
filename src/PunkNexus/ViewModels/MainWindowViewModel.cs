using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PunkNexus.Services;

namespace PunkNexus.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IUpdateOverlayPreview
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _installWatch;

    /// <summary>Last seen fingerprint of globalgamemanagers, so a patch is noticed while idle.</summary>
    private (long Length, DateTime WrittenUtc) _buildStamp;

    public GameSession Session { get; } = new();
    public SetupViewModel Setup { get; }
    public ModsViewModel Mods { get; }
    public ServersViewModel Servers { get; }
    public SettingsViewModel SettingsPage { get; }

    /// <summary>The overlay shown while the client downloads and installs its own replacement.</summary>
    public UpdateProgressViewModel Update { get; } = new();

    /// <summary>
    /// Drives that overlay from the diagnostic harness, so the one screen that otherwise only
    /// appears during a real self-replacement can be looked at and screenshotted without cutting a
    /// release to trigger it. Diagnostic mode only -- nothing in the app calls this.
    /// </summary>
    void IUpdateOverlayPreview.PreviewUpdateOverlay(string? stage, double? fraction)
    {
        if (stage is null)
        {
            Update.End();
            return;
        }

        if (!Update.IsRunning) Update.Begin(UpdateService.Current);
        Update.Report(new InstallProgress(stage, fraction));
    }

    [ObservableProperty] private bool _isSetupVisible = true;
    [ObservableProperty] private bool _isModsTab = true;
    [ObservableProperty] private bool _isServersTab;
    [ObservableProperty] private bool _isSettingsTab;

    public string AppVersion => AppServices.Version;
    public DialogService Dialogs => _services.Dialogs;

    /// <summary>Raised when the user declines the disclaimer; the window closes.</summary>
    public event Action? ShutdownRequested;

    public MainWindowViewModel() : this(AppServices.Create()) { }

    public MainWindowViewModel(AppServices services)
    {
        _services = services;

        Setup = new SetupViewModel(services.Settings);
        Mods = new ModsViewModel(services, Session);
        Servers = new ServersViewModel(services, Session)
        {
            SwapChanged = () => Mods.RefreshInstalledState(),
        };
        SettingsPage = new SettingsViewModel(services, Session);

        // The installer reports what it verified; the shell is what actually shows it.
        services.Installer.ConfirmDownload = ShowDownloadReportAsync;

        // The game exiting is what gives the user their own mods back. Nothing else asks for it, so
        // if this handler is ever lost the restore falls to the startup check below.
        services.Launcher.Exited += () => Dispatcher.UIThread.Post(() =>
        {
            Session.IsGameRunning = false;
            _ = RestoreModsAsync();
        });

        // Set the flag from the launch itself rather than waiting for the poll: five seconds of a
        // button that still says "Launch game" after the click is exactly the window in which
        // someone clicks it a second time.
        services.Launcher.Started += () => Dispatcher.UIThread.Post(() => Session.IsGameRunning = true);

        // A swap must never outlive the game, and the player should never have to ask for that.
        // Exited covers the ordinary case and startup covers a crashed client, but neither covers
        // a swap that is applied while the game never actually starts, or a game closed by
        // something other than the process Nexus is holding. This sweep closes that without a
        // button: if mods are set aside and no game is running, put them back.
        _swapWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _swapWatch.Tick += (_, _) =>
        {
            // Refresh the shared running flag first — this is the only poll, and both the sweep
            // below and every button depend on it.
            RefreshGameRunning();
            CheckBuildStamp();

            // Gated on "is PUNK open", NOT on "did Nexus start it". Those differ in exactly the
            // case that hurts: the client is restarted while the game is up, so it holds no process
            // handle, and a handle-based check would call that "not running" and pull the server's
            // mods out from under a live game.
            if (!Session.HasPath || Session.IsGameRunning) return;
            if (!_services.Play.HasSwap(Session.Path!)) return;
            _ = RestoreModsAsync();
        };
        _swapWatch.Start();

        // Before the player can do anything with a build we may be about to replace.
        Dispatcher.UIThread.Post(() => _ = OfferUpdateAsync());

        // The Refresh button is the natural "update what you know" gesture, and it used to update
        // everything except the one number the whole page is scored against.
        Mods.BeforeRefresh = RedetectBuildAsync;

        Setup.Completed += OnSetupCompleted;
        SettingsPage.GameFolderChanged += () => _ = ReloadForSessionAsync();
        SettingsPage.SetupRequested += ReturnToSetup;

        // "The install went away" is a real case — Steam verify, an uninstall, an unplugged drive.
        // Poll cheaply so the app drops back to setup instead of failing at install time.
        _installWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _installWatch.Tick += (_, _) => CheckInstallStillPresent();
    }

    public async Task InitializeAsync()
    {
        // Nothing else happens until the risk warning is accepted — including finding the game.
        if (!await AcceptDisclaimerAsync().ConfigureAwait(true))
        {
            ShutdownRequested?.Invoke();
            return;
        }

        var configured = _services.Settings.Current.GamePath;
        var verification = GameLocator.Verify(configured);

        if (verification.IsValid)
        {
            await EnterMainAsync(verification.Path).ConfigureAwait(true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(configured))
            Log.Info($"Configured game folder {configured} is no longer valid — returning to setup.");

        IsSetupVisible = true;
        await Setup.ScanAsync().ConfigureAwait(true);
    }

    private async Task<bool> AcceptDisclaimerAsync()
    {
        if (_services.Settings.Current.DisclaimerAcceptedVersion >= Disclaimer.Version) return true;

        var accepted = await _services.Dialogs.ShowAsync(Disclaimer.Request).ConfigureAwait(true);
        if (!accepted)
        {
            Log.Info("Disclaimer declined; exiting.");
            return false;
        }

        _services.Settings.Current.DisclaimerAcceptedVersion = Disclaimer.Version;
        _services.Settings.Save();
        Log.Info($"Disclaimer v{Disclaimer.Version} accepted.");
        return true;
    }

    /// <summary>
    /// Shows what verification found between download and extraction.
    ///
    /// A checksum mismatch gets ONE button. The install is already refused by the installer at that
    /// point, so offering "Install anyway" would be a lie about what pressing it does — and a
    /// dialog whose accept button does nothing is worse than a dialog that admits the decision was
    /// made for you. The panel stays red and says why.
    /// </summary>
    private Task<bool> ShowDownloadReportAsync(DownloadReport report)
    {
        var request = new DialogRequest
        {
            Title = report.Headline,
            Message = report.Message,
            Kind = report.Kind,
            Details = report.Details
                .Append(new DialogDetail($"{report.FileName} · {DownloadReport.FormatSize(report.SizeBytes)}"))
                .ToList(),
            AcceptText = report.Blocks ? "Close" : "Install",
            DeclineText = report.Blocks ? null : "Cancel",
        };

        return _services.Dialogs.ShowAsync(request);
    }

    private void OnSetupCompleted(string path) => _ = EnterMainAsync(path);

    private async Task EnterMainAsync(string path)
    {
        Session.Path = path;
        Session.LoaderInstalled = _services.Installer.IsLoaderInstalled(path);

        // Read the game's version before anything is listed — every compatibility decision below
        // depends on it, and a wrong answer here silently mis-gates the whole catalog.
        _buildStamp = GameVersionDetector.Stamp(path);
        Session.Build = await Task.Run(() => GameVersionDetector.Detect(path)).ConfigureAwait(true);

        IsSetupVisible = false;
        IsModsTab = true;

        SettingsPage.Refresh();
        _installWatch.Start();

        // A swap still in effect means a previous run did not get to put the user's mods back —
        // the client was closed while playing, or it crashed. Undo it before anything is listed,
        // so what the Mods tab shows is the user's own set and not a server's.
        await RestoreModsAsync().ConfigureAwait(true);

        await ReloadForSessionAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Puts the user's own mods back after a server visit. Safe to call at any time — it returns
    /// immediately when nothing is owed — and deliberately not gated on how the game was closed.
    /// </summary>
    private async Task RestoreModsAsync()
    {
        if (!Session.HasPath || !_services.Play.HasSwap(Session.Path!)) return;

        var server = _services.Play.SwapServerName(Session.Path!);
        Log.Info($"Restoring the mod set that was set aside for \"{server}\".");

        try
        {
            await _services.Play
                .RestoreAsync(Session.Path!, null, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("Restoring the user's mods failed", ex);
        }

        Mods.RefreshInstalledState();
    }

    private readonly DispatcherTimer? _swapWatch;

    /// <summary>
    /// Offer the update once, at startup, and close if it is declined.
    ///
    /// Deliberately not a dismissible notice. This client decides what a player installs and judges
    /// whether those downloads are what they claim; an old copy is missing whatever the newer one
    /// learned about refusing bad ones. "Later" on that is a choice to keep running the version
    /// with the known hole, so the two honest options are update or stop.
    ///
    /// A failed check is not a refusal to run: CheckAsync returns null when it cannot reach GitHub,
    /// and being offline must never lock someone out of their own mod manager.
    /// </summary>
    private async Task OfferUpdateAsync()
    {
        // Before the check, not after it: a run killed mid-download leaves a staged file behind,
        // and the tidy-up should happen whether or not there is a new release to fetch today.
        UpdateService.SweepStagedDownloads();

        var update = await _services.Updates.CheckAsync(CancellationToken.None).ConfigureAwait(true);
        if (update is null) return;

        var accepted = await _services.Dialogs.ShowAsync(new DialogRequest
        {
            Title = $"Update to {update.Version}",
            Message = $"You are running {UpdateService.Current}. PUNK Nexus keeps itself current "
                      + "because it decides what gets installed on your machine and checks that "
                      + "those downloads are what they claim to be.",
            Kind = DialogKind.Info,
            Details = new List<DialogDetail>
            {
                new("The download is verified against the checksum published with the release", null),
                new("PUNK Nexus restarts itself once the update is in place", null),
                new("Declining closes PUNK Nexus — your mods and game are untouched either way", null),
                // Said before it happens, not after. The binary is unsigned, so Windows may show
                // "Windows protected your PC" on the replacement. A security warning nobody warned
                // you about reads as evidence something is wrong; the same warning, predicted, with
                // a checksum you can check, reads as what it is.
                new("Windows may warn that the new file is unrecognised — it is unsigned. Its "
                    + "checksum is verified against the one published with the release before it "
                    + "is installed.", null),
            },
            AcceptText = "Update and restart",
            DeclineText = "Close",
        }).ConfigureAwait(true);

        if (!accepted)
        {
            Log.Info($"User declined the update to {update.Version}; closing.");
            Shutdown();
            return;
        }

        // Constructed on the UI thread so Progress<T> marshals every report back to it; the download
        // loop itself runs off it.
        var progress = new Progress<InstallProgress>(Update.Report);

        while (true)
        {
            Update.Begin(update.Version);

            try
            {
                await _services.Updates.ApplyAsync(update, progress, CancellationToken.None).ConfigureAwait(true);
                Shutdown();      // the swap script is waiting for this process to exit
                return;
            }
            catch (Exception ex)
            {
                // Down before the error goes up, or the explanation of what went wrong appears
                // behind a progress bar frozen at whatever fraction it failed on.
                Update.End();
                Log.Error($"Updating to {update.Version} failed", ex);

                // Declining the OFFER still closes the app -- that is a choice to keep running a
                // build we have reason to replace, and the policy above is deliberate. A FAILURE is
                // not that choice. The user said yes; the client could not deliver. Closing on it
                // punishes them for the network, and because the same thing happens on every
                // launch, it is not a bad session -- it is an app that can never be opened again.
                // The download has a hard ceiling of one HttpClient timeout with no resume, so a
                // slow enough line makes that permanent. Retrying is the fix; carrying on with the
                // old build is the fallback, said plainly rather than dressed up as fine.
                var retry = await _services.Dialogs.ShowAsync(new DialogRequest
                {
                    Title = "The update could not be applied",
                    Message = ex.Message,
                    Kind = DialogKind.Warning,
                    Details = new List<DialogDetail>
                    {
                        new("Nothing was installed and nothing was changed — the download was "
                            + "discarded", null),
                        new($"Continuing keeps you on {UpdateService.Current}, which is missing "
                            + $"whatever {update.Version} fixed", null),
                        new("The update is offered again next time PUNK Nexus starts", null),
                    },
                    AcceptText = "Try again",
                    DeclineText = "Continue without updating",
                }).ConfigureAwait(true);

                if (retry) continue;

                Log.Warn($"Continuing on {UpdateService.Current} after a failed update to {update.Version}.");
                return;
            }
        }
    }

    private static void Shutdown()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private async Task ReloadForSessionAsync()
    {
        // Before the catalog, not after: every row's compatibility badge is computed against the
        // detected build, so refreshing the list against a stale number just renders the wrong
        // answer faster.
        await RedetectBuildAsync().ConfigureAwait(true);

        SettingsPage.Refresh();
        await Mods.RefreshAsync().ConfigureAwait(true);
        await Servers.RefreshAsync().ConfigureAwait(true);
    }

    private void ReturnToSetup()
    {
        _installWatch.Stop();
        Session.Path = null;
        Session.LoaderInstalled = false;
        IsSetupVisible = true;
        Setup.Revalidate();
        _ = Setup.ScanAsync();
    }

    /// <summary>
    /// Answers "is PUNK open from this install" by looking for the process, not by consulting a
    /// handle we may not hold. Cheap enough to poll: one process enumeration every five seconds.
    /// </summary>
    private void RefreshGameRunning()
    {
        var running = Session.HasPath && GameLauncher.IsRunningFrom(Session.Path);
        if (running == Session.IsGameRunning) return;

        Session.IsGameRunning = running;
        Log.Info(running ? "PUNK is running." : "PUNK is no longer running.");

        // A game that closed may have closed because Steam wanted to patch it. Re-read the build
        // rather than keep showing the number from before.
        if (!running) _ = RedetectBuildAsync();
    }

    /// <summary>
    /// Notices a Steam patch with no user action at all: the file the version is read from is
    /// stat'd each poll, and only a change re-parses it. Without this the only way to learn the
    /// game had been patched was to close a game or press Refresh — so a client left open through
    /// an update kept gating every install on a version that was no longer installed.
    /// </summary>
    private void CheckBuildStamp()
    {
        if (!Session.HasPath) return;

        // Deliberately does NOT record the new stamp — RedetectBuildAsync owns that. Recording it
        // here made this a no-op: the re-detect it triggered found the stamp already up to date,
        // took its own early return, and nothing was ever re-read. Two guards over one piece of
        // state, each satisfied by the other.
        if (GameVersionDetector.Stamp(Session.Path!) == _buildStamp) return;

        _ = RedetectBuildAsync();
    }

    /// <summary>
    /// Re-reads the game's version from the install. Called whenever the game stops and on every
    /// catalog refresh, because Steam patches silently and a version read once at startup is a
    /// number that quietly stops being true — while every compatibility badge keeps trusting it.
    /// </summary>
    private async Task RedetectBuildAsync()
    {
        if (!Session.HasPath) return;

        // Guarded on the file's fingerprint, so the ordinary case — nothing has changed — costs one
        // stat instead of re-parsing the blob. Startup used to read it twice for exactly this
        // reason: EnterMain detected, then the catalog load detected the same file again.
        var stamp = GameVersionDetector.Stamp(Session.Path!);
        if (stamp == _buildStamp && Session.Build.HasVersion) return;

        _buildStamp = stamp;
        var before = Session.Build;
        var now = await Task.Run(() => GameVersionDetector.Detect(Session.Path!)).ConfigureAwait(true);
        if (now.Version == before.Version && now.SteamBuildId == before.SteamBuildId) return;

        Log.Info($"Game build changed: {before.Display} -> {now.Display}");
        Session.Build = now;
        Mods.RefreshCompatibility();
    }

    private void CheckInstallStillPresent()
    {
        if (IsSetupVisible || !Session.HasPath) return;
        if (GameLocator.QuickCheck(Session.Path)) return;

        Log.Warn($"Game folder {Session.Path} disappeared — returning to setup.");
        ReturnToSetup();
    }

    partial void OnIsModsTabChanged(bool value)
    {
        if (value) Mods.RefreshInstalledState();
    }

    partial void OnIsSettingsTabChanged(bool value)
    {
        if (value) SettingsPage.Refresh();
    }
}
