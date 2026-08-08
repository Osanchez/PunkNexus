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
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(AppServices.Create()),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
