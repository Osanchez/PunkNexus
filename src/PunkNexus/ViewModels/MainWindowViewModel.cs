using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PunkNexus.Services;

namespace PunkNexus.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _installWatch;

    public GameSession Session { get; } = new();
    public SetupViewModel Setup { get; }
    public ModsViewModel Mods { get; }
    public ServersViewModel Servers { get; }
    public SettingsViewModel SettingsPage { get; }

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
        services.Launcher.Exited += () => Dispatcher.UIThread.Post(() => _ = RestoreModsAsync());

        // A swap must never outlive the game, and the player should never have to ask for that.
        // Exited covers the ordinary case and startup covers a crashed client, but neither covers
        // a swap that is applied while the game never actually starts, or a game closed by
        // something other than the process Nexus is holding. This sweep closes that without a
        // button: if mods are set aside and no game is running, put them back.
        _swapWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _swapWatch.Tick += (_, _) =>
        {
            if (!Session.HasPath || _services.Launcher.IsRunning) return;
            if (!_services.Play.HasSwap(Session.Path!)) return;
            _ = RestoreModsAsync();
        };
        _swapWatch.Start();

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

    private async Task ReloadForSessionAsync()
    {
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
