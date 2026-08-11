using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PunkNexus.Models;
using PunkNexus.Services;

namespace PunkNexus.ViewModels;

public sealed partial class ModsViewModel : ViewModelBase
{
    private const string AllCategories = "All categories";

    private readonly AppServices _services;
    private readonly GameSession _session;
    private readonly List<ModRowViewModel> _all = new();

    private LoaderEntry? _loader;

    /// <summary>
    /// The published scan index, or null until one has been fetched. Null is meaningful: it is the
    /// difference between "no scan exists" and "we could not find out", and the install dialog
    /// says nothing at all in the second case rather than implying the first.
    /// </summary>
    private ScanIndex? _scans;

    public ObservableCollection<ModRowViewModel> Visible { get; } = new();
    public ObservableCollection<string> Categories { get; } = new() { AllCategories };

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _selectedCategory = AllCategories;
    [ObservableProperty] private bool _installedOnly;
    [ObservableProperty] private bool _compatibleOnly;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _loaderBusy;
    [ObservableProperty] private string? _loaderBusyText;
    [ObservableProperty] private string? _loaderError;

    public ModsViewModel(AppServices services, GameSession session)
    {
        _services = services;
        _session = session;

        // The installer reports on the scan alongside the checksum. It asks through a hook so that
        // a missing or unreachable reports file can never hold up an install.
        _services.Installer.PublishedScans = () => _scans;

        // Both halves of NeedsLoader live on the session. Watching only LoaderInstalled misses the
        // first entry into the shell: the path arrives, but LoaderInstalled is set false-to-false
        // and raises nothing, so the banner would never appear.
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GameSession.LoaderInstalled)
                or nameof(GameSession.Path)
                or nameof(GameSession.HasPath))
            {
                OnPropertyChanged(nameof(NeedsLoader));
            }

            if (e.PropertyName is nameof(GameSession.Build))
            {
                OnPropertyChanged(nameof(GameVersionLabel));
                OnPropertyChanged(nameof(GameVersionDetail));
                OnPropertyChanged(nameof(GameVersionUnknown));
            }

            if (e.PropertyName is nameof(GameSession.IsGameRunning)
                or nameof(GameSession.Path)
                or nameof(GameSession.HasPath))
            {
                OnPropertyChanged(nameof(CanLaunch));
                OnPropertyChanged(nameof(LaunchLabel));
            }
        };
    }

    public bool NeedsLoader => _session.HasPath && !_session.LoaderInstalled;
    public bool IsEmpty => Visible.Count == 0 && !IsLoading;

    // ---------------------------------------------------------------- the loader on disk

    /// <summary>
    /// What BepInEx is actually installed, re-read whenever it could have changed. Held rather
    /// than computed per binding because every read touches the disk.
    /// </summary>
    [ObservableProperty] private LoaderStatus? _loaderStatus;

    /// <summary>Always shown once a game folder is known — "BepInEx 6.0.0-be.785", or the reason
    /// there is no version to show. A version the user can read is the first thing anybody asks
    /// for when a mod does not load.</summary>
    public string LoaderVersionLabel => LoaderStatus switch
    {
        null => "BepInEx: checking…",
        { Installed: false } => "BepInEx: not installed",
        { InstalledVersion: null } => "BepInEx: installed, version unreadable",
        var s => $"BepInEx {s.InstalledVersion}",
    };

    /// <summary>The warning banner, distinct from the "not installed yet" one: this is for an
    /// install that EXISTS and will not do what the user expects.</summary>
    public bool LoaderNeedsAttention =>
        _session.HasPath && LoaderStatus is { Installed: true, UpdateAvailable: true };

    public string LoaderWarning => LoaderStatus?.Headline ?? "";

    /// <summary>
    /// Why an update is safe to accept, spelled out on the button's own banner. People are right
    /// to hesitate before letting something rewrite their game folder, and "your mods are kept" is
    /// exactly the fact that decides it.
    /// </summary>
    public string LoaderUpdateReassurance =>
        "Your installed mods and their settings are kept — only BepInEx's own files are replaced.";

    private void RefreshLoaderStatus()
    {
        LoaderStatus = LoaderInspector.Inspect(_session.Path, _loader?.Version);
        OnPropertyChanged(nameof(LoaderVersionLabel));
        OnPropertyChanged(nameof(LoaderNeedsAttention));
        OnPropertyChanged(nameof(LoaderWarning));
    }

    public int InstalledCount => _all.Count(m => m.IsInstalled);
    public int TotalCount => _all.Count;

    /// <summary>How many listed mods cannot be installed on this game build.</summary>
    public int UncertainCount => _all.Count(m => m.IsUncertain);
    public bool HasUncertain => UncertainCount > 0;

    public string GameVersionLabel => _session.Build.HasVersion
        ? $"Current Game Version {_session.Build.Version}"
        : "Current game version unknown";

    /// <summary>
    /// Where that number came from, on hover. The version gates what can be installed, so "which
    /// file was this read out of" is a fair question to be able to answer without reading the log —
    /// and it makes it self-evident that the number is the player's own install rather than
    /// anything the catalog asserts.
    /// </summary>
    public string GameVersionDetail
    {
        get
        {
            var build = _session.Build;
            if (!build.HasVersion)
                return $"Could not read a version from {GameLocator.DataDir}\\globalgamemanagers "
                     + "in your game folder. Nothing is blocked because of it.";

            var detail = $"Read from your install: {GameLocator.DataDir}\\globalgamemanagers";
            if (!string.IsNullOrWhiteSpace(build.SteamBuildId))
                detail += $"\nSteam build {build.SteamBuildId}, from the app manifest beside the install";
            return detail;
        }
    }

    public bool GameVersionUnknown => !_session.Build.HasVersion;

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();
    partial void OnInstalledOnlyChanged(bool value) => ApplyFilter();
    partial void OnCompatibleOnlyChanged(bool value) => ApplyFilter();

    /// <summary>
    /// Run by the shell before a refresh, to re-read the installed game version. Set by the shell
    /// because detection belongs to the session, not to this page — but this page owns the button
    /// the user presses to say "check again".
    /// </summary>
    public Func<Task>? BeforeRefresh { get; set; }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (BeforeRefresh is not null) await BeforeRefresh().ConfigureAwait(true);

        IsLoading = true;
        Notice = null;
        try
        {
            _services.Resolver.Invalidate();

            var result = await _services.Manifests
                .LoadRegistryAsync(forceRefresh: true, CancellationToken.None)
                .ConfigureAwait(true);

            _loader = result.Value.Loader;
            Notice = result.Warning;

            // Now that the catalog has said which version it expects, the install on disk can be
            // judged against it rather than merely described.
            RefreshLoaderStatus();

            // One request for the whole catalog's scan reports, before the rows are built, so a
            // row never briefly claims "not scanned yet" only to correct itself a moment later.
            _scans = await LoadScansAsync().ConfigureAwait(true);

            _all.Clear();
            foreach (var entry in result.Value.Mods.Where(m => m.Enabled))
            {
                var row = new ModRowViewModel(entry, _services, _session, ReportAsync)
                {
                    ScansAvailable = _scans is not null,
                    Scan = _scans?.Find(entry.Id),
                };
                _all.Add(row);
            }

            var byId = _all.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var row in _all)
                row.LookupMod = id => byId.GetValueOrDefault(id);

            RebuildCategories();
            ApplyFilter();

            // Each row fetches its own manifest. The list is usable immediately and every row
            // settles into its real version and compatibility as its fetch lands.
            _ = ResolveAllAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Loading the mod catalog failed", ex);
            Notice = $"Could not load the mod list: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            NotifyCounts();
        }
    }

    [RelayCommand]
    private Task InstallLoaderAsync() => RunLoaderInstallAsync(replaceExistingCore: false);

    /// <summary>
    /// Update in place: same download, same verification, but the old core is cleared first so a
    /// BepInEx 5 install cannot survive underneath a 6 one. Plugins and config are untouched.
    /// </summary>
    [RelayCommand]
    private Task UpdateLoaderAsync() => RunLoaderInstallAsync(replaceExistingCore: true);

    private async Task RunLoaderInstallAsync(bool replaceExistingCore)
    {
        if (string.IsNullOrWhiteSpace(_session.Path)) return;

        if (_loader is null)
        {
            LoaderError = "The catalog does not list a BepInEx download.";
            return;
        }

        LoaderError = null;
        LoaderBusy = true;

        var progress = new Progress<InstallProgress>(p => LoaderBusyText = p.Stage);

        try
        {
            var installed = await _services.Installer
                .InstallLoaderAsync(_session.Path!, _loader, progress, CancellationToken.None,
                    replaceExistingCore)
                .ConfigureAwait(true);

            _session.LoaderInstalled = _services.Installer.IsLoaderInstalled(_session.Path!);
            Status = installed
                ? replaceExistingCore
                    ? $"BepInEx updated to {_loader.Version}. Your mods were kept — launch the game once."
                    : "BepInEx installed. Launch the game once, then install mods."
                : replaceExistingCore
                    ? "BepInEx was not updated."
                    : "BepInEx was not installed.";
        }
        catch (Exception ex)
        {
            var verb = replaceExistingCore ? "update" : "install";
            LoaderError = ex is InstallException ex2 ? ex2.Message : $"Could not {verb} BepInEx: {ex.Message}";
            Log.Error($"{char.ToUpperInvariant(verb[0])}{verb[1..]}ing BepInEx failed", ex);
        }
        finally
        {
            LoaderBusy = false;
            LoaderBusyText = null;
            OnPropertyChanged(nameof(NeedsLoader));
            RefreshLoaderStatus();
        }
    }

    public void RefreshInstalledState()
    {
        foreach (var row in _all) row.RefreshInstalledState();
        NotifyCounts();
    }

    /// <summary>
    /// Re-scores every row after the detected game build changes. The filter is re-applied because
    /// "Compatible only" is a filter over exactly this result — leaving it alone would keep showing
    /// a list selected against the previous version.
    /// </summary>
    public void RefreshCompatibility()
    {
        foreach (var row in _all) row.RefreshCompatibility();
        ApplyFilter();
        NotifyCounts();
    }

    // ---------------------------------------------------------------- launching

    /// <summary>
    /// Start the game from here, so installing mods and playing them is not two separate errands
    /// through two different launchers.
    /// </summary>
    [RelayCommand]
    private void LaunchGame()
    {
        if (!CanLaunch) return;
        try
        {
            _services.Launcher.Launch(_session.Path!);
        }
        catch (Exception ex)
        {
            Log.Error("Could not launch the game", ex);
            Status = null;
            Notice = ex.Message;
        }
    }

    /// <summary>
    /// Off while PUNK is open. Launching a second copy over a running one is never what was meant,
    /// and it is the specific mistake a button that stays enabled invites: the first click appears
    /// to do nothing for the several seconds Unity takes to show a window.
    /// </summary>
    public bool CanLaunch => _session.HasPath && !_session.IsGameRunning;

    /// <summary>
    /// The button says what is true now, rather than narrating what was asked for. "Launching…"
    /// that never changes is worse than no label at all -- it is indistinguishable from a launch
    /// that silently failed, so the honest states are only two: it is running, or you can start it.
    /// </summary>
    public string LaunchLabel => _session.IsGameRunning ? "PUNK is running" : "Launch game";

    // ---------------------------------------------------------------- server visits

    /// <summary>
    /// Set while a server's mod set is loaded in place of the user's own. The Mods tab is where
    /// someone would first notice their mods "missing", so the explanation belongs here.
    /// </summary>


    /// <summary>
    /// Fetches the scan index, treating any failure as "unknown" rather than as "unscanned".
    /// Nothing in the client depends on it, so a failure here is silent by design — a red banner
    /// about a missing reports file would tell the user something they cannot act on.
    /// </summary>
    private async Task<ScanIndex?> LoadScansAsync()
    {
        try
        {
            var result = await _services.Manifests
                .LoadScanIndexAsync(forceRefresh: true, CancellationToken.None)
                .ConfigureAwait(true);

            Log.Info($"Loaded {result.Value.Scans.Count} scan report(s) ({result.Origin}).");

            // Embedded is the "everything else failed" tier, and there is deliberately no scan
            // index compiled into the exe — so reaching it means the file could not be read at
            // all, which is unknown, not empty. An index that genuinely lists nothing (before the
            // first scheduled run) arrives over the network and is kept.
            return result.Origin == ManifestOrigin.Embedded ? null : result.Value;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not load virus scan reports: {ex.Message}");
            return null;
        }
    }

    private async Task ResolveAllAsync()
    {
        foreach (var row in _all.ToList())
        {
            try
            {
                await row.ResolveAsync(CancellationToken.None).ConfigureAwait(true);
                _ = row.LoadIconAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not resolve {row.Id}: {ex.Message}");
            }
        }

        NotifyCounts();
        ApplyFilter();
    }

    private Task ReportAsync(string message)
    {
        Status = message;
        RefreshInstalledState();
        return Task.CompletedTask;
    }

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(UncertainCount));
        OnPropertyChanged(nameof(HasUncertain));
        OnPropertyChanged(nameof(GameVersionLabel));
        OnPropertyChanged(nameof(GameVersionUnknown));
    }

    private void RebuildCategories()
    {
        var wanted = _all.Select(m => m.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var previous = SelectedCategory;

        Categories.Clear();
        Categories.Add(AllCategories);
        foreach (var category in wanted) Categories.Add(category);

        SelectedCategory = Categories.Contains(previous) ? previous : AllCategories;
    }

    private void ApplyFilter()
    {
        var term = Search.Trim();

        IEnumerable<ModRowViewModel> query = _all;

        if (!string.IsNullOrEmpty(term))
            query = query.Where(m =>
                m.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                m.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                m.Author.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                m.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                m.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)));

        if (!string.Equals(SelectedCategory, AllCategories, StringComparison.Ordinal))
            query = query.Where(m => string.Equals(m.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));

        if (InstalledOnly) query = query.Where(m => m.IsInstalled);

        // Installed mods stay visible even when blocked, so a mod that the game has outgrown can
        // still be found and removed.
        if (CompatibleOnly) query = query.Where(m => !m.IsUncertain || m.IsInstalled);

        Visible.Clear();
        foreach (var row in query.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            Visible.Add(row);

        OnPropertyChanged(nameof(IsEmpty));
    }
}
