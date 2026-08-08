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

    public MainWindowViewModel() : this(AppServices.Create()) { }

    public MainWindowViewModel(AppServices services)
    {
        _services = services;

        Setup = new SetupViewModel(services.Settings);
        Mods = new ModsViewModel(services, Session);
        Servers = new ServersViewModel(services);
        SettingsPage = new SettingsViewModel(services, Session);

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

    private void OnSetupCompleted(string path) => _ = EnterMainAsync(path);

    private async Task EnterMainAsync(string path)
    {
        Session.Path = path;
        Session.LoaderInstalled = _services.Installer.IsLoaderInstalled(path);

        // Read the game's version before anything is listed — every compatibility decision below
        // depends on it, and a wrong answer here silently mis-gates the whole catalogue.
        Session.Build = await Task.Run(() => GameVersionDetector.Detect(path)).ConfigureAwait(true);

        IsSetupVisible = false;
        IsModsTab = true;

        SettingsPage.Refresh();
        _installWatch.Start();

        await ReloadForSessionAsync().ConfigureAwait(true);
    }

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
