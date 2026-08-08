using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PunkNexus.Models;
using PunkNexus.Services;

namespace PunkNexus.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly GameSession _session;

    /// <summary>Set by the view; opens the platform folder picker.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>Raised when the game folder changes, so the shell can reload against it.</summary>
    public event Action? GameFolderChanged;

    /// <summary>Raised when the configured folder stops being valid — sends the user back to setup.</summary>
    public event Action? SetupRequested;

    public ObservableCollection<VerificationCheck> Checks { get; } = new();

    [ObservableProperty] private string? _gamePath;
    [ObservableProperty] private bool _isVerified;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string _manifestBaseUrl = "";
    [ObservableProperty] private bool _loaderInstalled;
    [ObservableProperty] private string? _loaderVersion;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _busyText;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _error;

    public SettingsViewModel(AppServices services, GameSession session)
    {
        _services = services;
        _session = session;
        ManifestBaseUrl = services.Settings.Current.ManifestBaseUrl;
    }

    public string AppVersion => AppServices.Version;
    public string DataFolder => AppPaths.Root;

    /// <summary>The detected game build — what every mod's declared gameVersion is matched against.</summary>
    public string GameBuildDisplay => _session.Build.Display;
    public bool GameBuildKnown => _session.Build.HasVersion;
    public bool IsManifestUrlCustom =>
        !string.Equals(ManifestBaseUrl?.Trim(), AppSettings.DefaultManifestBaseUrl, StringComparison.OrdinalIgnoreCase);

    public void Refresh()
    {
        GamePath = _session.Path;
        Checks.Clear();

        if (string.IsNullOrWhiteSpace(GamePath))
        {
            IsVerified = false;
            Problem = "No game folder is configured.";
            return;
        }

        var result = GameLocator.Verify(GamePath);
        foreach (var check in result.Checks) Checks.Add(check);

        IsVerified = result.IsValid;
        Problem = result.Problem;

        LoaderInstalled = result.IsValid && _services.Installer.IsLoaderInstalled(GamePath!);
        LoaderVersion = _services.State.Load(GamePath!).Loader?.Version;

        OnPropertyChanged(nameof(GameBuildDisplay));
        OnPropertyChanged(nameof(GameBuildKnown));
    }

    [RelayCommand]
    private async Task ChangeFolderAsync()
    {
        if (PickFolder is null) return;

        Error = null;
        Status = null;

        try
        {
            var picked = await PickFolder().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(picked)) return;

            var result = GameLocator.Verify(picked);
            if (!result.IsValid)
            {
                // Refuse rather than store a folder that will fail at install time.
                Error = result.Problem ?? "That folder is not a PUNK install.";
                return;
            }

            _services.Settings.Current.GamePath = result.Path;
            _services.Settings.Save();
            _session.Path = result.Path;
            _session.LoaderInstalled = _services.Installer.IsLoaderInstalled(result.Path);

            Log.Info($"Game folder changed to {result.Path}.");
            Status = "Game folder updated.";
            Refresh();
            GameFolderChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Changing the game folder failed", ex);
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void Verify()
    {
        Error = null;
        Refresh();

        if (IsVerified)
        {
            Status = "Install verified.";
            return;
        }

        Status = null;
        SetupRequested?.Invoke();
    }

    [RelayCommand]
    private async Task ReinstallLoaderAsync()
    {
        if (string.IsNullOrWhiteSpace(GamePath)) return;

        Error = null;
        Status = null;
        Busy = true;

        var progress = new Progress<InstallProgress>(p => BusyText = p.Stage);

        try
        {
            var manifest = await _services.Manifests
                .LoadRegistryAsync(forceRefresh: false, CancellationToken.None)
                .ConfigureAwait(true);

            if (manifest.Value.Loader is null)
            {
                Error = "The catalog does not list a BepInEx download.";
                return;
            }

            await _services.Installer
                .InstallLoaderAsync(GamePath!, manifest.Value.Loader, progress, CancellationToken.None)
                .ConfigureAwait(true);

            _session.LoaderInstalled = _services.Installer.IsLoaderInstalled(GamePath!);
            Status = "BepInEx reinstalled.";
            Refresh();
        }
        catch (Exception ex)
        {
            Error = ex is InstallException ? ex.Message : $"Could not reinstall BepInEx: {ex.Message}";
            Log.Error("Reinstalling BepInEx failed", ex);
        }
        finally
        {
            Busy = false;
            BusyText = null;
        }
    }

    [RelayCommand]
    private void SaveManifestUrl()
    {
        Error = null;

        var url = ManifestBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            url = AppSettings.DefaultManifestBaseUrl;
            ManifestBaseUrl = url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            Error = "That is not a valid http(s) URL.";
            return;
        }

        _services.Settings.Current.ManifestBaseUrl = url!;
        _services.Settings.Save();
        _services.Resolver.Invalidate();
        Status = "Catalog source saved. Refresh the Mods tab to reload.";
        OnPropertyChanged(nameof(IsManifestUrlCustom));
    }

    [RelayCommand]
    private void ResetManifestUrl()
    {
        ManifestBaseUrl = AppSettings.DefaultManifestBaseUrl;
        SaveManifestUrl();
    }

    [RelayCommand]
    private void ClearCache()
    {
        Error = null;
        try
        {
            if (Directory.Exists(AppPaths.CacheDir)) Directory.Delete(AppPaths.CacheDir, recursive: true);
            AppPaths.EnsureCreated();
            _services.Resolver.Invalidate();
            Status = "Cached catalog and icons cleared.";
        }
        catch (Exception ex)
        {
            Error = $"Could not clear the cache: {ex.Message}";
            Log.Error("Clearing the cache failed", ex);
        }
    }

    [RelayCommand]
    private void OpenGameFolder() => OpenInShell(GamePath);

    [RelayCommand]
    private void OpenDataFolder() => OpenInShell(AppPaths.Root);

    private void OpenInShell(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {path}: {ex.Message}");
            Error = $"Could not open that folder: {ex.Message}";
        }
    }

    partial void OnManifestBaseUrlChanged(string value) => OnPropertyChanged(nameof(IsManifestUrlCustom));
}
