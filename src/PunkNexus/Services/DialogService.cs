using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PunkNexus.Services;

public enum DialogKind { Info, Success, Warning, Danger }

/// <summary>One line of supporting detail, rendered with a pass/fail marker.</summary>
public sealed record DialogDetail(string Text, bool? Ok = null)
{
    // Split out for XAML, which cannot branch on a nullable bool.
    public bool IsOk => Ok == true;
    public bool IsBad => Ok == false;
    public bool IsNeutral => Ok is null;
}

public sealed class DialogRequest
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public DialogKind Kind { get; init; } = DialogKind.Info;
    public IReadOnlyList<DialogDetail> Details { get; init; } = Array.Empty<DialogDetail>();
    public string AcceptText { get; init; } = "OK";

    /// <summary>When null the dialog has a single button and cannot be refused.</summary>
    public string? DeclineText { get; init; }

    /// <summary>
    /// An optional "go and read the source" button. Added for the virus scan report, where the
    /// honest thing is to hand the user the underlying VirusTotal page rather than ask them to
    /// take this window's summary of it on faith.
    /// </summary>
    public string? LinkText { get; init; }
    public string? LinkUrl { get; init; }

    public bool HasDecline => !string.IsNullOrWhiteSpace(DeclineText);
    public bool HasDetails => Details.Count > 0;
    public bool HasLink => !string.IsNullOrWhiteSpace(LinkUrl) && !string.IsNullOrWhiteSpace(LinkText);

    public bool IsSuccess => Kind == DialogKind.Success;
    public bool IsWarning => Kind == DialogKind.Warning;
    public bool IsDanger => Kind == DialogKind.Danger;
    public bool IsInfo => Kind == DialogKind.Info;
}

/// <summary>
/// Lets any layer ask the user something without knowing the UI exists. The window renders
/// <see cref="Current"/> as an overlay; the caller just awaits a bool.
/// </summary>
public sealed partial class DialogService : ObservableObject
{
    // One dialog at a time. Installs run concurrently with dependency pulls, and two overlays
    // fighting over the same slot would strand whichever lost.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<bool>? _pending;

    [ObservableProperty] private DialogRequest? _current;

    public bool IsOpen => Current is not null;

    public async Task<bool> ShowAsync(DialogRequest request)
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Current = request;
            return await _pending.Task.ConfigureAwait(true);
        }
        finally
        {
            Current = null;
            _pending = null;
            _gate.Release();
        }
    }

    /// <summary>
    /// Opens the current dialog's link in the user's browser, leaving the dialog open — reading
    /// the source material should not cost them the window they were reading.
    /// </summary>
    [RelayCommand]
    private void OpenLink()
    {
        var url = Current?.LinkUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {url}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Accept() => Close(true);

    [RelayCommand]
    private void Decline() => Close(false);

    private void Close(bool result) => _pending?.TrySetResult(result);

    partial void OnCurrentChanged(DialogRequest? value) => OnPropertyChanged(nameof(IsOpen));
}
