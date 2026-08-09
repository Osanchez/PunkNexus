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
                OnPropertyChanged(nameof(GameVersionLabel));
        };
    }

    public bool NeedsLoader => _session.HasPath && !_session.LoaderInstalled;
    public bool IsEmpty => Visible.Count == 0 && !IsLoading;
    public int InstalledCount => _all.Count(m => m.IsInstalled);
    public int TotalCount => _all.Count;

    /// <summary>How many listed mods cannot be installed on this game build.</summary>
    public int UncertainCount => _all.Count(m => m.IsUncertain);
    public bool HasUncertain => UncertainCount > 0;

    public string GameVersionLabel => _session.Build.HasVersion
        ? $"Game {_session.Build.Version}"
        : "Game version unknown";

    public bool GameVersionUnknown => !_session.Build.HasVersion;

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();
    partial void OnInstalledOnlyChanged(bool value) => ApplyFilter();
    partial void OnCompatibleOnlyChanged(bool value) => ApplyFilter();

    [RelayCommand]
    public async Task RefreshAsync()
    {
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

            _all.Clear();
            foreach (var entry in result.Value.Mods.Where(m => m.Enabled))
                _all.Add(new ModRowViewModel(entry, _services, _session, ReportAsync));

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
    private async Task InstallLoaderAsync()
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
                .InstallLoaderAsync(_session.Path!, _loader, progress, CancellationToken.None)
                .ConfigureAwait(true);

            _session.LoaderInstalled = _services.Installer.IsLoaderInstalled(_session.Path!);
            Status = installed
                ? "BepInEx installed. Launch the game once, then install mods."
                : "BepInEx was not installed.";
        }
        catch (Exception ex)
        {
            LoaderError = ex is InstallException ? ex.Message : $"Could not install BepInEx: {ex.Message}";
            Log.Error("Installing BepInEx failed", ex);
        }
        finally
        {
            LoaderBusy = false;
            LoaderBusyText = null;
            OnPropertyChanged(nameof(NeedsLoader));
        }
    }

    public void RefreshInstalledState()
    {
        foreach (var row in _all) row.RefreshInstalledState();
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
        if (!_session.HasPath) return;
        try
        {
            _services.Launcher.Launch(_session.Path!);
            Status = "PUNK is starting…";
        }
        catch (Exception ex)
        {
            Log.Error("Could not launch the game", ex);
            Status = null;
            Notice = ex.Message;
        }
    }

    public bool CanLaunch => _session.HasPath;

    // ---------------------------------------------------------------- server visits

    /// <summary>
    /// Set while a server's mod set is loaded in place of the user's own. The Mods tab is where
    /// someone would first notice their mods "missing", so the explanation belongs here.
    /// </summary>
    [ObservableProperty] private string? _swapNotice;

    public bool HasSwapNotice => !string.IsNullOrWhiteSpace(SwapNotice);
    partial void OnSwapNoticeChanged(string? value) => OnPropertyChanged(nameof(HasSwapNotice));

    public void RefreshSwapState()
    {
        if (!_session.HasPath || !_services.Play.HasSwap(_session.Path!))
        {
            SwapNotice = null;
            return;
        }

        var server = _services.Play.SwapServerName(_session.Path!);
        SwapNotice = string.IsNullOrWhiteSpace(server)
            ? "Your own mods are set aside for a server visit. They are safe and can be restored."
            : $"Your own mods are set aside so you can play on \"{server}\". They are safe — "
              + "restore them whenever you like, or just finish playing and they come back on their own.";
    }

    /// <summary>Put the user's mods back without waiting for the game to close.</summary>
    [RelayCommand]
    private async Task RestoreMyModsAsync()
    {
        if (!_session.HasPath) return;

        LoaderBusy = true;
        LoaderBusyText = "Restoring your mods";
        try
        {
            var progress = new Progress<InstallProgress>(p => LoaderBusyText = p.Stage);
            var complete = await _services.Play
                .RestoreAsync(_session.Path!, progress, CancellationToken.None)
                .ConfigureAwait(true);

            Status = complete
                ? "Your mods are back."
                : "Some folders could not be moved back — close the game and try again.";
        }
        catch (Exception ex)
        {
            Log.Error("Restoring mods failed", ex);
            Notice = $"Could not restore your mods: {ex.Message}";
        }
        finally
        {
            LoaderBusy = false;
            LoaderBusyText = null;
            RefreshSwapState();
            RefreshInstalledState();
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
