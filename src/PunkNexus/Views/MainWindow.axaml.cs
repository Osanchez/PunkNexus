using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PunkNexus.ViewModels;

namespace PunkNexus.Views;

public partial class MainWindow : Window
{
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (_initialized || DataContext is not MainWindowViewModel vm) return;
        _initialized = true;

        // The picker needs a TopLevel, which only the view has.
        vm.Setup.PickFolder = PickGameFolderAsync;
        vm.SettingsPage.PickFolder = PickGameFolderAsync;

        // Declining the disclaimer closes the client rather than dropping into a disabled shell.
        vm.ShutdownRequested += Close;

        await vm.InitializeAsync();
    }

    private async Task<string?> PickGameFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select your PUNK install folder (the one containing Punk.exe)",
            AllowMultiple = false,
        });

        var folder = folders.Count > 0 ? folders[0] : null;
        return folder?.TryGetLocalPath();
    }
}
