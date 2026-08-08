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

    public bool HasPath => !string.IsNullOrWhiteSpace(Path);

    partial void OnPathChanged(string? value) => OnPropertyChanged(nameof(HasPath));
}
