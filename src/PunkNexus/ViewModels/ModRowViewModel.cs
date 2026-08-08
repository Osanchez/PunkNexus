using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PunkNexus.Models;
using PunkNexus.Services;

namespace PunkNexus.ViewModels;

public sealed partial class ModRowViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly GameSession _session;
    private readonly Func<string, Task> _report;

    public ModEntry Entry { get; }

    /// <summary>Resolves a mod id to its row, so an install can satisfy its own dependencies.</summary>
    public Func<string, ModRowViewModel?>? LookupMod { get; set; }

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private string? _installedVersion;
    [ObservableProperty] private string? _latestVersion;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate = true;
    [ObservableProperty] private Bitmap? _icon;
    [ObservableProperty] private string? _error;

    public ModRowViewModel(ModEntry entry, AppServices services, GameSession session, Func<string, Task> report)
    {
        Entry = entry;
        _services = services;
        _session = session;
        _report = report;
        LatestVersion = entry.Version;
    }

    public string Name => Entry.Name;
    public string Id => Entry.Id;
    public string Author => string.IsNullOrWhiteSpace(Entry.Author) ? "Unknown" : Entry.Author!;
    public string Category => string.IsNullOrWhiteSpace(Entry.Category) ? "Other" : Entry.Category!;
    public string Description => Entry.Description ?? "";
    public string? Homepage => Entry.Homepage;
    public bool HasHomepage => !string.IsNullOrWhiteSpace(Entry.Homepage);

    public bool HasUpdate =>
        IsInstalled &&
        !string.IsNullOrWhiteSpace(LatestVersion) &&
        !string.IsNullOrWhiteSpace(InstalledVersion) &&
        !string.Equals(LatestVersion, InstalledVersion, StringComparison.OrdinalIgnoreCase);

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public string VersionLabel => IsInstalled && !string.IsNullOrWhiteSpace(InstalledVersion)
        ? $"v{InstalledVersion}"
        : !string.IsNullOrWhiteSpace(LatestVersion) ? $"v{LatestVersion}" : "";

    public string StatusLabel =>
        !IsInstalled ? "Not installed"
        : HasUpdate ? $"Update available — v{LatestVersion}"
        : "Installed";

    public string PrimaryActionLabel => HasUpdate ? "Update" : "Install";

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
    /// Stable per-mod colour. Uses an explicit sum rather than string.GetHashCode, which is
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

    public void RefreshInstalledState()
    {
        var path = _session.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            IsInstalled = false;
            InstalledVersion = null;
            return;
        }

        IsInstalled = _services.Installer.IsModInstalled(path!, Entry);
        InstalledVersion = IsInstalled ? _services.Installer.GetInstalledVersion(path!, Entry) : null;
        NotifyDerived();
    }

    /// <summary>Records the version actually published, so the row reflects the real download.</summary>
    public void ApplyResolvedVersion(string? version)
    {
        if (!string.IsNullOrWhiteSpace(version)) LatestVersion = version;
        NotifyDerived();
    }

    public async Task LoadIconAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Entry.IconUrl)) return;
        Icon = await _services.Icons.GetAsync(Entry.IconUrl, ct).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (string.IsNullOrWhiteSpace(_session.Path)) return;

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
            foreach (var dependencyId in Entry.Dependencies)
            {
                var dependency = LookupMod?.Invoke(dependencyId);
                if (dependency is null || dependency.IsInstalled) continue;

                BusyText = $"Installing {dependency.Name} (required by {Name})…";
                await _services.Installer
                    .InstallModAsync(_session.Path!, dependency.Entry, progress, CancellationToken.None)
                    .ConfigureAwait(true);

                dependency.RefreshInstalledState();
                pulled.Add(dependency.Name);
            }

            await _services.Installer
                .InstallModAsync(_session.Path!, Entry, progress, CancellationToken.None)
                .ConfigureAwait(true);

            RefreshInstalledState();

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
                .UninstallModAsync(_session.Path!, Entry, CancellationToken.None)
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
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(PrimaryActionLabel));
    }

    partial void OnIsInstalledChanged(bool value) => NotifyDerived();
    partial void OnInstalledVersionChanged(string? value) => NotifyDerived();
    partial void OnLatestVersionChanged(string? value) => NotifyDerived();
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
