using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PunkNexus.Models;
using PunkNexus.Services;

namespace PunkNexus.ViewModels;

/// <summary>
/// Reads the published server list. The client only ever pulls this document — it never contacts a
/// game server directly, so servers are not exposed to the browser's traffic.
/// </summary>
public sealed partial class ServersViewModel : ViewModelBase
{
    private const string AnyMode = "Any mode";
    private const string AnyMod = "Any mod";

    private readonly AppServices _services;
    private readonly List<ServerEntry> _all = new();

    public ObservableCollection<ServerEntry> Visible { get; } = new();
    public ObservableCollection<string> GameModes { get; } = new() { AnyMode };
    public ObservableCollection<string> ModFilters { get; } = new() { AnyMod };

    [ObservableProperty] private string _nameFilter = "";
    [ObservableProperty] private string _addressFilter = "";
    [ObservableProperty] private string _minPlayersText = "";
    [ObservableProperty] private string _selectedGameMode = AnyMode;
    [ObservableProperty] private string _selectedMod = AnyMod;
    [ObservableProperty] private bool _hideEmpty;
    [ObservableProperty] private bool _hideFull;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string? _updatedUtc;

    public ServersViewModel(AppServices services) => _services = services;

    /// <summary>Nothing is published at all — the feed is reachable but has no entries.</summary>
    public bool NothingPublished => _all.Count == 0 && !IsLoading;

    /// <summary>Servers exist but the filters exclude them all. Distinct from the case above.</summary>
    public bool NoMatches => _all.Count > 0 && Visible.Count == 0 && !IsLoading;

    public bool HasResults => Visible.Count > 0;
    public int MatchCount => Visible.Count;

    partial void OnNameFilterChanged(string value) => ApplyFilter();
    partial void OnAddressFilterChanged(string value) => ApplyFilter();
    partial void OnMinPlayersTextChanged(string value) => ApplyFilter();
    partial void OnSelectedGameModeChanged(string value) => ApplyFilter();
    partial void OnSelectedModChanged(string value) => ApplyFilter();
    partial void OnHideEmptyChanged(bool value) => ApplyFilter();
    partial void OnHideFullChanged(bool value) => ApplyFilter();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        Notice = null;
        try
        {
            var result = await _services.Manifests
                .LoadServersAsync(forceRefresh: true, CancellationToken.None)
                .ConfigureAwait(true);

            Notice = result.Warning;
            UpdatedUtc = result.Value.UpdatedUtc;

            _all.Clear();
            _all.AddRange(result.Value.Servers);

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

    [RelayCommand]
    private void ClearFilters()
    {
        NameFilter = "";
        AddressFilter = "";
        MinPlayersText = "";
        SelectedGameMode = AnyMode;
        SelectedMod = AnyMod;
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
    }

    private void ApplyFilter()
    {
        IEnumerable<ServerEntry> query = _all;

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
