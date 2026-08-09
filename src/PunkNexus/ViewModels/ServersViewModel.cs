using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PunkNexus.Models;
using PunkNexus.Services;

namespace PunkNexus.ViewModels;

/// <summary>
/// The server browser. Two sources feed one list, because the two transports have genuinely
/// different discovery models:
///
///   Steam sessions  — read live from Valve's lobby list. A lobby dies with its last member, so
///                     what is shown is what exists; there is no cache to go stale.
///   Dedicated (UDP) — read from the published list. Not wired up yet; see docs/SERVER_LIST.md.
///
/// Neither path contacts a game server to build the list.
/// </summary>
public sealed partial class ServersViewModel : ViewModelBase
{
    private const string AnyMode = "Any mode";
    private const string AnyMod = "Any mod";
    public const string SourceAll = "All sources";
    public const string SourceSteam = "Steam";
    public const string SourceDedicated = "Self-hosted";

    private readonly AppServices _services;
    private readonly GameSession _session;
    private readonly List<ServerEntry> _all = new();

    public ObservableCollection<ServerEntry> Visible { get; } = new();
    public ObservableCollection<string> GameModes { get; } = new() { AnyMode };
    public ObservableCollection<string> ModFilters { get; } = new() { AnyMod };
    public ObservableCollection<string> Sources { get; } = new() { SourceAll, SourceSteam, SourceDedicated };

    [ObservableProperty] private string _nameFilter = "";
    [ObservableProperty] private string _addressFilter = "";
    [ObservableProperty] private string _minPlayersText = "";
    [ObservableProperty] private string _selectedGameMode = AnyMode;
    [ObservableProperty] private string _selectedMod = AnyMod;
    [ObservableProperty] private string _selectedSource = SourceAll;
    [ObservableProperty] private bool _hideEmpty;
    [ObservableProperty] private bool _hideFull;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string? _steamNotice;
    [ObservableProperty] private string? _updatedUtc;

    public ServersViewModel(AppServices services, GameSession session)
    {
        _services = services;
        _session = session;
    }

    public bool NothingPublished => _all.Count == 0 && !IsLoading;
    public bool NoMatches => _all.Count > 0 && Visible.Count == 0 && !IsLoading;
    public bool HasResults => Visible.Count > 0;
    public int MatchCount => Visible.Count;
    public bool HasSteamNotice => !string.IsNullOrWhiteSpace(SteamNotice);

    public int SteamCount => _all.Count(s => s.Source == ServerSource.Steam);
    public int DedicatedCount => _all.Count(s => s.Source == ServerSource.Dedicated);

    partial void OnNameFilterChanged(string value) => ApplyFilter();
    partial void OnAddressFilterChanged(string value) => ApplyFilter();
    partial void OnMinPlayersTextChanged(string value) => ApplyFilter();
    partial void OnSelectedGameModeChanged(string value) => ApplyFilter();
    partial void OnSelectedModChanged(string value) => ApplyFilter();
    partial void OnSelectedSourceChanged(string value) => ApplyFilter();
    partial void OnHideEmptyChanged(bool value) => ApplyFilter();
    partial void OnHideFullChanged(bool value) => ApplyFilter();
    partial void OnSteamNoticeChanged(string? value) => OnPropertyChanged(nameof(HasSteamNotice));

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        Notice = null;
        try
        {
            _all.Clear();

            // Published list first: it renders instantly and never fails hard.
            var published = await _services.Manifests
                .LoadServersAsync(forceRefresh: true, CancellationToken.None)
                .ConfigureAwait(true);

            Notice = published.Warning;
            UpdatedUtc = published.Value.UpdatedUtc;
            _all.AddRange(published.Value.Servers);

            await AddSteamSessionsAsync().ConfigureAwait(true);

            RebuildFilterOptions();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Log.Error("Loading the server list failed", ex);
            Notice = $"Could not load the server list: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            NotifyEmptyStates();
        }
    }

    /// <summary>
    /// Steam is optional. Not running is the ordinary case for someone who only came to install
    /// mods, so it explains itself in a notice and the published list is still shown.
    /// </summary>
    private async Task AddSteamSessionsAsync()
    {
        SteamNotice = null;

        if (!_session.HasPath)
        {
            SteamNotice = "Steam sessions need a verified game folder.";
            return;
        }

        if (!_services.Steam.TryInitialize(_session.Path!))
        {
            SteamNotice = _services.Steam.Status switch
            {
                SteamStatus.NotRunning =>
                    "Steam is not running, so live Steam sessions are not listed. Start Steam and refresh.",
                SteamStatus.NoLibrary =>
                    "Steam browsing needs steam_api64.dll from your game folder, which was not found there.",
                SteamStatus.Unsupported =>
                    "Steam browsing is only available on Windows builds.",
                _ => _services.Steam.StatusDetail ?? "Steam is unavailable.",
            };
            return;
        }

        try
        {
            var sessions = await _services.Steam.BrowseAsync(CancellationToken.None).ConfigureAwait(true);
            _all.AddRange(sessions);
        }
        catch (Exception ex)
        {
            Log.Error("Browsing Steam lobbies failed", ex);
            SteamNotice = $"Could not read Steam sessions: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        NameFilter = "";
        AddressFilter = "";
        MinPlayersText = "";
        SelectedGameMode = AnyMode;
        SelectedMod = AnyMod;
        SelectedSource = SourceAll;
        HideEmpty = false;
        HideFull = false;
    }

    private void RebuildFilterOptions()
    {
        var modes = _all.Select(s => s.GameMode)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase);

        GameModes.Clear();
        GameModes.Add(AnyMode);
        foreach (var mode in modes) GameModes.Add(mode);
        SelectedGameMode = AnyMode;

        var mods = _all.SelectMany(s => s.Mods)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase);

        ModFilters.Clear();
        ModFilters.Add(AnyMod);
        foreach (var mod in mods) ModFilters.Add(mod);
        SelectedMod = AnyMod;
    }

    private void NotifyEmptyStates()
    {
        OnPropertyChanged(nameof(NothingPublished));
        OnPropertyChanged(nameof(NoMatches));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(SteamCount));
        OnPropertyChanged(nameof(DedicatedCount));
    }

    private void ApplyFilter()
    {
        IEnumerable<ServerEntry> query = _all;

        if (!string.Equals(SelectedSource, SourceAll, StringComparison.Ordinal))
        {
            var wanted = string.Equals(SelectedSource, SourceSteam, StringComparison.Ordinal)
                ? ServerSource.Steam
                : ServerSource.Dedicated;
            query = query.Where(s => s.Source == wanted);
        }

        if (!string.IsNullOrWhiteSpace(NameFilter))
            query = query.Where(s => s.Name.Contains(NameFilter.Trim(), StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(AddressFilter))
            query = query.Where(s => (s.Address ?? "").Contains(AddressFilter.Trim(), StringComparison.OrdinalIgnoreCase)
                                     || s.Endpoint.Contains(AddressFilter.Trim(), StringComparison.OrdinalIgnoreCase));

        if (int.TryParse(MinPlayersText, out var minPlayers))
            query = query.Where(s => s.Players >= minPlayers);

        if (!string.Equals(SelectedGameMode, AnyMode, StringComparison.Ordinal))
            query = query.Where(s => string.Equals(s.GameMode, SelectedGameMode, StringComparison.OrdinalIgnoreCase));

        if (!string.Equals(SelectedMod, AnyMod, StringComparison.Ordinal))
            query = query.Where(s => s.Mods.Any(m => string.Equals(m, SelectedMod, StringComparison.OrdinalIgnoreCase)));

        if (HideEmpty) query = query.Where(s => s.Players > 0);
        if (HideFull) query = query.Where(s => s.MaxPlayers == 0 || s.Players < s.MaxPlayers);

        Visible.Clear();
        foreach (var server in query.OrderByDescending(s => s.Players).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            Visible.Add(server);

        NotifyEmptyStates();
        OnPropertyChanged(nameof(MatchCount));
    }
}
