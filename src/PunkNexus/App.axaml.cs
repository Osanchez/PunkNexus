using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PunkNexus.Services;
using PunkNexus.ViewModels;
using PunkNexus.Views;

namespace PunkNexus;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Log.Info($"PUNK Nexus {AppServices.Version} starting.");
            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(AppServices.Create()),
            };
            desktop.MainWindow = window;

            // Opt-in remote control, for testing the Play flow without a human clicking it. Gated
            // on PUNKNEXUS_DIAG=1 so it does not exist for a normal user. Attached on Opened
            // because it walks the visual tree, and there is not one until the window is shown.
            window.Opened += (_, _) => DiagHarness.MaybeStart(window);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
