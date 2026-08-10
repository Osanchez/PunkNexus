using CommunityToolkit.Mvvm.ComponentModel;
using PunkNexus.Services;

namespace PunkNexus.ViewModels;

/// <summary>
/// What the window shows while the client is replacing itself.
///
/// Deliberately not a <see cref="DialogService"/> request. That type exists to ask a question and
/// wait for an answer; this asks nothing, has no buttons, and is not dismissible -- the user already
/// consented on the dialog that preceded it. What it owes them from that point is evidence that
/// something is still happening, because the alternative is what this replaced: an accepted update
/// that left the window sitting inert for the length of the download and then closed without a word.
/// </summary>
public sealed partial class UpdateProgressViewModel : ViewModelBase
{
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _stage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private double _percent;

    /// <summary>
    /// Starts indeterminate and stays that way until a stage reports a fraction. A bar pinned at
    /// zero while the connection is opening looks like a bar that is stuck.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeterminate))]
    private bool _isIndeterminate = true;

    /// <summary>Exists so the percentage label can bind to it directly. A percentage next to a bar
    /// that is sweeping rather than filling is a number describing nothing.</summary>
    public bool IsDeterminate => !IsIndeterminate;

    public string PercentText => $"{Percent:0}%";

    public void Begin(Version version)
    {
        Title = $"Updating to {version}";
        Stage = "Starting…";
        Percent = 0;
        IsIndeterminate = true;
        IsRunning = true;
    }

    /// <summary>
    /// Mirrors the mod rows: a stage carrying a fraction switches the bar to determinate, one
    /// without switches it back. Verifying and installing follow a download that reached 100%, and
    /// showing them under a full bar would claim they had finished too.
    /// </summary>
    public void Report(InstallProgress progress)
    {
        Stage = progress.Stage;

        if (progress.Fraction is { } fraction)
        {
            IsIndeterminate = false;
            Percent = Math.Clamp(fraction, 0, 1) * 100;
        }
        else
        {
            IsIndeterminate = true;
        }
    }

    /// <summary>
    /// Only for the failure path. A successful update never calls this: the app shuts down so the
    /// swap can happen, and the overlay is the right last thing on screen while it does.
    /// </summary>
    public void End()
    {
        IsRunning = false;
        Stage = string.Empty;
    }
}
