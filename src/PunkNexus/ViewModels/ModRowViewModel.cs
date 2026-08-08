using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PunkNexus.Models;
using PunkNexus.Services;

namespace PunkNexus.ViewModels;

/// <summary>
/// One catalog row. Holds all three tiers at once — the registry listing, the manifest the mod
/// publishes, and the manifest found in the install — and every piece of state below is derived
/// from comparing them.
/// </summary>
public sealed partial class ModRowViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly GameSession _session;
    private readonly Func<string, Task> _report;

    public RegistryEntry Registry { get; }

    /// <summary>Resolves a mod id to its row, so an install can satisfy its own dependencies.</summary>
    public Func<string, ModRowViewModel?>? LookupMod { get; set; }

    [ObservableProperty] private ModManifest? _published;
    [ObservableProperty] private ModManifest? _installed;
    [ObservableProperty] private CompatibilityResult? _compatibility;
    [ObservableProperty] private bool _isResolving = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private Bitmap? _icon;
    [ObservableProperty] private string? _error;

    public ModRowViewModel(RegistryEntry registry, AppServices services, GameSession session, Func<string, Task> report)
    {
        Registry = registry;
        _services = services;
        _session = session;
        _report = report;
    }

    // ------------------------------------------------------------ presentation

    public string Id => Registry.Id;
    public string Name => Published?.Name is { Length: > 0 } n ? n : Registry.Name;
    public string Author => Published?.Author ?? Registry.Author ?? "Unknown";
    public string Category => Published?.Category ?? Registry.Category ?? "Other";
    public string Description => Published?.Description ?? Registry.Description ?? "";
    public string PluginFolder => Published?.EffectivePluginFolder ?? Registry.Id;
    public IReadOnlyList<string> Tags => Published?.Tags.Count > 0 ? Published.Tags : Registry.Tags;

    // ------------------------------------------------------------ state

    public bool IsInstalled => Installed is not null ||
                               (_session.HasPath && InstallService.IsModInstalled(_session.Path!, PluginFolder));

    public string? InstalledVersion => Installed?.Version;
    public string? PublishedVersion => Published?.Version;

    /// <summary>The manifest could not be fetched, so nothing about this mod is trustworthy.</summary>
    public bool IsUnavailable => !IsResolving && Published is null;

    public bool HasUpdate =>
        IsInstalled &&
        !string.IsNullOrWhiteSpace(PublishedVersion) &&
        !string.IsNullOrWhiteSpace(InstalledVersion) &&
        !string.Equals(PublishedVersion, InstalledVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>Exact-match gate: a mismatch blocks the install outright.</summary>
    public bool IsBlocked => Compatibility?.Blocks == true;

    public bool CanInstall => !IsBusy && !IsBlocked && !IsUnavailable && Published is not null;

    /// <summary>
    /// The copy on disk was built for a different game version than the one now installed — the
    /// game updated underneath it. Distinct from a catalog mismatch, and worth its own warning
    /// because the mod is live in the user's game right now.
    /// </summary>
    public bool InstalledIsStale =>
        IsInstalled &&
        Installed is not null &&
        _session.Build.HasVersion &&
        !string.IsNullOrWhiteSpace(Installed.GameVersion) &&
        !string.Equals(Installed.GameVersion, _session.Build.Version, StringComparison.OrdinalIgnoreCase);

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool ShowCompatibilityBadge => Compatibility?.IsWarning == true && !IsResolving;

    public string CompatibilityText => Compatibility?.Summary ?? "";

    public string CompatibilityBadge => Compatibility?.State switch
    {
        CompatibilityState.Incompatible => "wrong game version",
        CompatibilityState.Undeclared => "no game version declared",
        CompatibilityState.UnknownGame => "not checked",
        _ => "",
    };

    public string VersionLabel =>
        IsInstalled && !string.IsNullOrWhiteSpace(InstalledVersion) ? $"v{InstalledVersion}"
        : !string.IsNullOrWhiteSpace(PublishedVersion) ? $"v{PublishedVersion}"
        : "";

    public string StatusLabel =>
        IsResolving ? "Checking…"
        : IsUnavailable ? "Manifest unavailable"
        : !IsInstalled && IsBlocked ? "Cannot install"
        : !IsInstalled ? "Not installed"
        : InstalledIsStale ? "Installed — game has moved on"
        : HasUpdate ? $"Update available — v{PublishedVersion}"
        : "Installed";

    /// <summary>Two-letter monogram, used when a mod has no icon so the row never looks broken.</summary>
    public string Monogram
    {
        get
        {
            var words = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "?";
            if (words.Length == 1) return words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant();
            return $"{words[0][0]}{words[1][0]}".ToUpperInvariant();
        }
    }

    private static readonly Color[] Palette =
    {
        Color.Parse("#FF2D6F"), Color.Parse("#D6FF3F"), Color.Parse("#00E0C6"),
        Color.Parse("#8B5CFF"), Color.Parse("#FF8A3D"), Color.Parse("#3DA5FF"),
    };

    /// <summary>
    /// Stable per-mod color. Uses an explicit sum rather than string.GetHashCode, which is
    /// randomized per process and would repaint the list on every launch.
    /// </summary>
    public IBrush MonogramBrush
    {
        get
        {
            var sum = Id.Aggregate(0, (acc, c) => acc + c);
            return new SolidColorBrush(Palette[sum % Palette.Length]);
        }
    }

    // ------------------------------------------------------------ refresh

    /// <summary>Fetches the published manifest and re-evaluates compatibility.</summary>
    public async Task ResolveAsync(CancellationToken ct)
    {
        IsResolving = true;
        try
        {
            Published = await _services.ModManifests.FetchAsync(Registry, ct).ConfigureAwait(true);
            Compatibility = Published is null
                ? null
                : CompatibilityCheck.Evaluate(Published.GameVersion, _session.Build);
        }
        finally
        {
            IsResolving = false;
            RefreshInstalledState();
        }
    }

    /// <summary>Re-reads what is actually on disk. Disk is the source of truth, not our records.</summary>
    public void RefreshInstalledState()
    {
        Installed = _session.HasPath
            ? ModManifestService.ReadInstalled(_session.Path!, PluginFolder)
            : null;

        NotifyDerived();
    }

    public async Task LoadIconAsync(CancellationToken ct)
    {
        var url = Published?.IconUrl ?? Registry.IconUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        Icon = await _services.Icons.GetAsync(url, ct).ConfigureAwait(true);
    }

    // ------------------------------------------------------------ commands

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (string.IsNullOrWhiteSpace(_session.Path) || Published is null) return;

        if (IsBlocked)
        {
            // The button is disabled in this state; this is the belt to that suspenders.
            Error = Compatibility?.Summary;
            return;
        }

        Error = null;
        IsBusy = true;
        IsProgressIndeterminate = true;
        BusyText = "Starting…";

        var progress = new Progress<InstallProgress>(p =>
        {
            BusyText = p.Stage;
            if (p.Fraction is { } fraction)
            {
                IsProgressIndeterminate = false;
                Progress = fraction * 100;
            }
            else
            {
                IsProgressIndeterminate = true;
            }
        });

        try
        {
            // Pull in anything this mod needs first. Installing a mod that silently does nothing
            // because its framework is missing is the single most common "it's broken" report.
            var pulled = new List<string>();
            foreach (var dependencyId in Published.Dependencies)
            {
                var dependency = LookupMod?.Invoke(dependencyId);
                if (dependency is null || dependency.IsInstalled) continue;

                if (dependency.Published is null || dependency.IsBlocked)
                    throw new InstallException(
                        $"{Name} needs {dependency.Name}, which cannot be installed on your game " +
                        $"version. {dependency.Compatibility?.Summary}".TrimEnd());

                BusyText = $"Installing {dependency.Name} (required by {Name})…";
                var dependencyInstalled = await _services.Installer
                    .InstallModAsync(_session.Path!, dependency.Published, progress, CancellationToken.None)
                    .ConfigureAwait(true);

                dependency.RefreshInstalledState();

                // Declining a dependency means declining this mod: installing it without its
                // framework would produce a mod that loads and does nothing.
                if (!dependencyInstalled)
                {
                    await _report($"{Name} was not installed — {dependency.Name} is required.").ConfigureAwait(true);
                    return;
                }

                pulled.Add(dependency.Name);
            }

            var installed = await _services.Installer
                .InstallModAsync(_session.Path!, Published, progress, CancellationToken.None)
                .ConfigureAwait(true);

            RefreshInstalledState();

            if (!installed)
            {
                await _report($"{Name} was not installed.").ConfigureAwait(true);
                return;
            }

            var message = pulled.Count > 0
                ? $"{Name} installed, along with {string.Join(" and ", pulled)} which it needs."
                : $"{Name} installed.";
            await _report(message).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Error = ex is InstallException ? ex.Message : $"Install failed: {ex.Message}";
            Log.Error($"Installing {Id} failed", ex);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            NotifyDerived();
        }
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (string.IsNullOrWhiteSpace(_session.Path)) return;

        Error = null;
        IsBusy = true;
        IsProgressIndeterminate = true;
        BusyText = $"Removing {Name}…";

        try
        {
            await _services.Installer
                .UninstallModAsync(_session.Path!, Id, PluginFolder, Name, CancellationToken.None)
                .ConfigureAwait(true);

            RefreshInstalledState();
            await _report($"{Name} removed.").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Error = ex is InstallException ? ex.Message : $"Could not remove: {ex.Message}";
            Log.Error($"Removing {Id} failed", ex);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            NotifyDerived();
        }
    }

    [RelayCommand]
    private void DismissError() => Error = null;

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Author));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(InstalledVersion));
        OnPropertyChanged(nameof(PublishedVersion));
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstalledIsStale));
        OnPropertyChanged(nameof(ShowCompatibilityBadge));
        OnPropertyChanged(nameof(CompatibilityText));
        OnPropertyChanged(nameof(CompatibilityBadge));
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(StatusLabel));
    }

    partial void OnPublishedChanged(ModManifest? value) => NotifyDerived();
    partial void OnInstalledChanged(ModManifest? value) => NotifyDerived();
    partial void OnCompatibilityChanged(CompatibilityResult? value) => NotifyDerived();
    partial void OnIsResolvingChanged(bool value) => NotifyDerived();
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
