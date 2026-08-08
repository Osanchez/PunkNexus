using CommunityToolkit.Mvvm.ComponentModel;

namespace PunkNexus.ViewModels;

/// <summary>The verified game folder the whole app works against, shared by every page.</summary>
public sealed partial class GameSession : ObservableObject
{
    [ObservableProperty] private string? _path;
    [ObservableProperty] private bool _loaderInstalled;

    public bool HasPath => !string.IsNullOrWhiteSpace(Path);

    partial void OnPathChanged(string? value) => OnPropertyChanged(nameof(HasPath));
}
