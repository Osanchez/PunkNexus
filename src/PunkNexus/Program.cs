using Avalonia;
using PunkNexus.Services;

namespace PunkNexus;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            AppPaths.EnsureCreated();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // A crash before the window exists leaves no other trace on a user's machine.
            Log.Error("PUNK Nexus failed to start", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
