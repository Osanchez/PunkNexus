using System.Net;
using System.Reflection;

namespace PunkNexus.Services;

/// <summary>Single composition point for the app's services.</summary>
public sealed class AppServices
{
    public HttpClient Http { get; }
    public SettingsService Settings { get; }
    public ManifestService Manifests { get; }
    public ModManifestService ModManifests { get; }
    public ReleaseResolver Resolver { get; }
    public InstallStateStore State { get; }
    public InstallService Installer { get; }
    public IconCache Icons { get; }
    public DialogService Dialogs { get; }
    public SteamBrowser Steam { get; }

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private AppServices()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        Http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

        // GitHub's API rejects requests without a User-Agent, and this is harmless everywhere else.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"PunkNexus/{Version}");

        // Deliberately no default Accept header. Mods may be hosted anywhere, and announcing
        // "application/vnd.github+json" to an unrelated file host is at best meaningless and at
        // worst a 406. The GitHub API call sets that header on its own request instead.

        Settings = new SettingsService();
        Settings.Load();

        Manifests = new ManifestService(Http, Settings);
        ModManifests = new ModManifestService(Http);
        Resolver = new ReleaseResolver(Http);
        State = new InstallStateStore();
        Installer = new InstallService(Http, Resolver, State);
        Icons = new IconCache(Http);
        Dialogs = new DialogService();
        Steam = new SteamBrowser();
    }

    public static AppServices Create() => new();
}
