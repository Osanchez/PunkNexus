using CommunityToolkit.Mvvm.ComponentModel;
using PunkNexus.Services;

namespace PunkNexus.ViewModels;

/// <summary>The verified game folder the whole app works against, shared by every page.</summary>
public sealed partial class GameSession : ObservableObject
{
    [ObservableProperty] private string? _path;
    [ObservableProperty] private bool _loaderInstalled;

    /// <summary>
    /// The version read out of the install. Every compatibility decision is made against this, so
    /// it is detected from disk rather than taken from the catalog.
    /// </summary>
    [ObservableProperty] private GameBuild _build = GameBuild.Unknown;

    /// <summary>
    /// Whether PUNK is open from this install — started from anywhere, not just from Nexus.
    ///
    /// One flag, owned by the shell, because three separate things need the same answer and would
    /// otherwise each get it slightly wrong: the Launch button, the Play button, and the sweep that
    /// puts a player's mods back. The last is the one that matters — restoring mods under a running
    /// game swaps files the game has already loaded and will write back over.
    /// </summary>
    [ObservableProperty] private bool _isGameRunning;

    public bool HasPath => !string.IsNullOrWhiteSpace(Path);

    partial void OnPathChanged(string? value) => OnPropertyChanged(nameof(HasPath));
}
