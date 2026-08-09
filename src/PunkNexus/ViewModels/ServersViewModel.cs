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
    private const string AnyRegion = "Any region";
    public const string SourceAll = "All sources";
    public const string SourceSteam = "Steam";
    public const string SourceDedicated = "Self-hosted";

    private readonly AppServices _services;
    private readonly GameSession _session;
    private readonly List<ServerEntry> _all = new();

    public ObservableCollection<ServerEntry> Visible { get; } = new();
    public ObservableCollection<string> GameModes { get; } = new() { AnyMode };
    public ObservableCollection<string> ModFilters { get; } = new() { AnyMod };
    public ObservableCollection<string> Regions { get; } = new() { AnyRegion };
    public ObservableCollection<string> Sources { get; } = new() { SourceAll, SourceSteam, SourceDedicated };

    [ObservableProperty] private string _nameFilter = "";
    [ObservableProperty] private string _addressFilter = "";
    [ObservableProperty] private string _minPlayersText = "";
    [ObservableProperty] private string _selectedGameMode = AnyMode;
    [ObservableProperty] private string _selectedMod = AnyMod;
    [ObservableProperty] private string _selectedRegion = AnyRegion;
    [ObservableProperty] private string _maxPingText = "";
    [ObservableProperty] private string _selectedSource = SourceAll;
    [ObservableProperty] private bool _hideEmpty;
    [ObservableProperty] private bool _hideFull;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string? _steamNotice;
    [ObservableProperty] private string? _updatedUtc;
    [ObservableProperty] private bool _isPlayBusy;
    [ObservableProperty] private string? _playStatus;

    /// <summary>Raised once a swap has been applied, so other tabs can reflect it.</summary>
    public Action? SwapChanged { get; set; }

    public ServersViewModel(AppServices services, GameSession session)
    {
        _services = services;
        _session = session;

        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(GameSession.IsGameRunning)) return;

            OnPropertyChanged(nameof(CanPlay));
            OnPropertyChanged(nameof(PlayBlockedNotice));
            OnPropertyChanged(nameof(IsPlayBlocked));

            // "Launched — joining X." describes a moment, not a state. Once the game is closed it
            // is a stale claim about a session that has ended, so it goes with the game.
            if (!_session.IsGameRunning) PlayStatus = null;
        };
    }

    public bool NothingPublished => _all.Count == 0 && !IsLoading;
    public bool NoMatches => _all.Count > 0 && Visible.Count == 0 && !IsLoading;
    public bool HasResults => Visible.Count > 0;
    public int MatchCount => Visible.Count;
    public bool HasSteamNotice => !string.IsNullOrWhiteSpace(SteamNotice);

    /// <summary>Exposed as a positive because a compiled binding cannot negate through a cast,
    /// which is what a row's Play button has to do to reach this page's state.</summary>
    public bool CanPlay => !IsPlayBusy && !_session.IsGameRunning;

    /// <summary>
    /// Play is off while the game is open, and this says why — an explanation the Launch button
    /// does not need because its own label can carry it.
    ///
    /// The reason is stronger than tidiness. Play may move plugin folders around to match the
    /// server, and doing that under a running game rearranges assemblies it has already loaded:
    /// the swap appears to work, the game is unaffected until it next writes, and the mods that
    /// come back afterwards are not the ones that were set aside.
    /// </summary>
    public bool IsPlayBlocked => _session.IsGameRunning;

    public string PlayBlockedNotice => _session.IsGameRunning
        ? "PUNK is open. Close the game to join a server from here — joining may change which mods "
          + "are installed, which cannot be done safely while it is running."
        : "";

    public int SteamCount => _all.Count(s => s.Source == ServerSource.Steam);
    public int DedicatedCount => _all.Count(s => s.Source == ServerSource.Dedicated);

    partial void OnNameFilterChanged(string value) => ApplyFilter();
    partial void OnAddressFilterChanged(string value) => ApplyFilter();
    partial void OnMinPlayersTextChanged(string value) => ApplyFilter();
    partial void OnSelectedGameModeChanged(string value) => ApplyFilter();
    partial void OnSelectedModChanged(string value) => ApplyFilter();
    partial void OnSelectedRegionChanged(string value) => ApplyFilter();
    partial void OnMaxPingTextChanged(string value) => ApplyFilter();
    partial void OnSelectedSourceChanged(string value) => ApplyFilter();
    partial void OnHideEmptyChanged(bool value) => ApplyFilter();
    partial void OnHideFullChanged(bool value) => ApplyFilter();
    partial void OnSteamNoticeChanged(string? value) => OnPropertyChanged(nameof(HasSteamNotice));
    partial void OnIsPlayBusyChanged(bool value) => OnPropertyChanged(nameof(CanPlay));

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

    // ---------------------------------------------------------------- play

    /// <summary>
    /// Match this install to the server's mod set, then launch straight into it.
    ///
    /// The plan is shown and consented to before anything moves, because the honest description of
    /// what this does is "temporarily replace your mods" — and a user who is not told that will
    /// reasonably think the client lost them.
    /// </summary>
    [RelayCommand]
    private async Task PlayAsync(ServerEntry? server)
    {
        if (server is null || IsPlayBusy) return;

        if (!_session.HasPath)
        {
            Notice = "Set up your game folder before joining a server.";
            return;
        }

        // Re-checked here and not only on the button: the poll that clears this runs every five
        // seconds, so a click can land in the window where the button is stale.
        if (_session.IsGameRunning)
        {
            Notice = PlayBlockedNotice;
            return;
        }

        IsPlayBusy = true;
        PlayStatus = null;
        try
        {
            var registry = await _services.Manifests
                .LoadRegistryAsync(forceRefresh: false, CancellationToken.None)
                .ConfigureAwait(true);

            var plan = _services.Play.Plan(_session.Path!, server.Mods, registry.Value.Mods);

            if (!await ConfirmPlanAsync(server, plan).ConfigureAwait(true)) return;

            if (!plan.IsReady)
            {
                var progress = new Progress<InstallProgress>(p => PlayStatus = p.Stage);
                await _services.Play
                    .ApplyAsync(_session.Path!, plan, server.Name, progress, CancellationToken.None)
                    .ConfigureAwait(true);
            }

            // Tell the Mods tab a swap is now in force. Without this the "Your mods are set
            // aside" banner -- and the Restore my mods button inside it -- never appeared while
            // swapped: RefreshSwapState was only ever called after a RESTORE, so the one state
            // that needs explaining was the one state never announced. A player whose mods had
            // just been moved saw them listed as "Not installed" with no explanation and no way
            // back except quitting the game.
            SwapChanged?.Invoke();

            PlayStatus = "Starting PUNK…";
            _services.Launcher.Launch(_session.Path!, JoinArgsFor(server));
            PlayStatus = $"Launched — joining {server.Name}.";
        }
        catch (Exception ex)
        {
            Log.Error($"Could not start play for {server.Name}", ex);
            PlayStatus = null;
            Notice = $"Could not join {server.Name}: {ex.Message}";
        }
        finally
        {
            IsPlayBusy = false;
        }
    }

    /// <summary>
    /// Both transports auto-join, by different arguments. A Steam session goes by lobby id through
    /// Steam's own +connect_lobby; a dedicated server goes by address through the mod's
    /// +punkmv_connect, which picks the transport from the target's shape. A row carrying neither
    /// launches plain and the player joins from the in-game screen.
    /// </summary>
    private static string? JoinArgsFor(ServerEntry server)
    {
        if (server.IsSteam && !string.IsNullOrWhiteSpace(server.Id))
            return GameLauncher.ConnectLobbyArgs(server.Id!);

        if (!string.IsNullOrWhiteSpace(server.Address))
            return GameLauncher.ConnectArgs($"{server.Address}:{server.Port}");

        return null;
    }

    private Task<bool> ConfirmPlanAsync(ServerEntry server, PlayPlan plan)
    {
        var details = plan.Steps()
            .Select(s => new DialogDetail(s.Summary, s.Ok))
            .ToList();

        if (plan.IsReady && !plan.HasGaps)
        {
            // Nothing to change. Launching without a dialog would be defensible, but this is the
            // one moment the user learns the client checked at all.
            details.Add(new DialogDetail("Your mods already match this server", true));
        }

        var message = plan.IsReady
            ? $"Your install already matches {server.Name}. PUNK will start and join it."
            : $"To join {server.Name}, your mods have to match the server's exactly — that is the "
              + "server's rule, not ours. Anything of yours that is in the way is moved aside, kept "
              + "safe, and put back automatically when you finish playing. Nothing is deleted.";

        return _services.Dialogs.ShowAsync(new DialogRequest
        {
            Title = plan.IsReady ? $"Join {server.Name}" : "Set up and join",
            Message = message,
            Kind = plan.HasGaps ? DialogKind.Warning : DialogKind.Info,
            Details = details,
            AcceptText = plan.IsReady ? "Launch" : "Set up and launch",
            DeclineText = "Cancel",
        });
    }

    [RelayCommand]
    private void ClearFilters()
    {
        NameFilter = "";
        AddressFilter = "";
        MinPlayersText = "";
        SelectedGameMode = AnyMode;
        SelectedMod = AnyMod;
        SelectedRegion = AnyRegion;
        MaxPingText = "";
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

        var regions = _all.Select(s => s.Region)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase);

        Regions.Clear();
        Regions.Add(AnyRegion);
        foreach (var region in regions) Regions.Add(region);
        SelectedRegion = AnyRegion;
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

        if (!string.Equals(SelectedRegion, AnyRegion, StringComparison.Ordinal))
            query = query.Where(s => string.Equals((s.Region ?? "").Trim(), SelectedRegion, StringComparison.OrdinalIgnoreCase));

        // A server whose ping could not be estimated is kept rather than hidden: an unknown ping is
        // not a bad one, and silently dropping a joinable server would be worse than showing "—".
        if (int.TryParse(MaxPingText, out var maxPing) && maxPing > 0)
            query = query.Where(s => s.PingMs is not int ms || ms <= maxPing);

        if (HideEmpty) query = query.Where(s => s.Players > 0);
        if (HideFull) query = query.Where(s => s.MaxPlayers == 0 || s.Players < s.MaxPlayers);

        Visible.Clear();
        foreach (var server in query
                     .OrderBy(s => s.PingMs ?? int.MaxValue)      // closest first; unknowns sink
                     .ThenByDescending(s => s.Players)
                     .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            Visible.Add(server);

        NotifyEmptyStates();
        OnPropertyChanged(nameof(MatchCount));
    }
}
