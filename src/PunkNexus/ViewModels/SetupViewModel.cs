using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PunkNexus.Services;

namespace PunkNexus.ViewModels;

/// <summary>
/// The gate. Nothing else in the app is reachable until a folder passes verification, and the
/// user can always override detection by browsing — detection is a convenience, not a requirement.
/// </summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private readonly SettingsService _settings;

    /// <summary>Set by the view; opens the platform folder picker.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>Raised with a verified path once the user confirms.</summary>
    public event Action<string>? Completed;

    public ObservableCollection<string> Detected { get; } = new();
    public ObservableCollection<VerificationCheck> Checks { get; } = new();

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string? _selectedPath;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _isVerified;
    [ObservableProperty] private bool _hasChecked;
    [ObservableProperty] private string _scanSummary = "";

    public SetupViewModel(SettingsService settings) => _settings = settings;

    public bool HasDetected => Detected.Count > 0;

    partial void OnSelectedPathChanged(string? value) => Verify(value);

    [RelayCommand]
    public async Task ScanAsync()
    {
        IsScanning = true;
        ScanSummary = "Looking for your PUNK install…";
        try
        {
            var found = await Task.Run(() => GameLocator.FindCandidates()).ConfigureAwait(true);

            Detected.Clear();
            foreach (var path in found) Detected.Add(path);
            OnPropertyChanged(nameof(HasDetected));

            ScanSummary = found.Count switch
            {
                0 => "No install found automatically — choose the folder yourself below.",
                1 => "Found your install.",
                _ => $"Found {found.Count} installs — pick the one you want to mod.",
            };

            // One unambiguous hit is the overwhelmingly common case; select it so the user only
            // has to confirm.
            if (found.Count > 0 && string.IsNullOrWhiteSpace(SelectedPath))
                SelectedPath = found[0];
        }
        catch (Exception ex)
        {
            Log.Error("Scan for the game folder failed", ex);
            ScanSummary = "Automatic detection failed — choose the folder yourself below.";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (PickFolder is null) return;

        try
        {
            var picked = await PickFolder().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(picked)) SelectedPath = picked;
        }
        catch (Exception ex)
        {
            Log.Error("Folder picker failed", ex);
            Problem = $"Could not open the folder picker: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Confirm()
    {
        if (!IsVerified || string.IsNullOrWhiteSpace(SelectedPath)) return;

        _settings.Current.GamePath = SelectedPath;
        _settings.Save();
        Log.Info($"Game folder set to {SelectedPath}.");
        Completed?.Invoke(SelectedPath!);
    }

    /// <summary>Re-runs verification against the current selection.</summary>
    public void Revalidate() => Verify(SelectedPath);

    private void Verify(string? path)
    {
        Checks.Clear();

        if (string.IsNullOrWhiteSpace(path))
        {
            HasChecked = false;
            IsVerified = false;
            Problem = null;
            ConfirmCommand.NotifyCanExecuteChanged();
            return;
        }

        var result = GameLocator.Verify(path);
        foreach (var check in result.Checks) Checks.Add(check);

        HasChecked = true;
        IsVerified = result.IsValid;
        Problem = result.Problem;
        ConfirmCommand.NotifyCanExecuteChanged();
    }
}
