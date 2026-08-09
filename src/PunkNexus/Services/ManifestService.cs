using System.Reflection;
using System.Text.Json;
using PunkNexus.Models;

namespace PunkNexus.Services;

public enum ManifestOrigin { Network, Cache, Embedded }

public sealed record ManifestResult<T>(T Value, ManifestOrigin Origin, string? Warning);

/// <summary>
/// Loads the catalog with three fallbacks: the network, then the last good copy on disk, then the
/// copy compiled into the exe. A user with no connection still gets a usable window.
/// </summary>
public sealed class ManifestService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly HttpClient _http;
    private readonly SettingsService _settings;

    public ManifestService(HttpClient http, SettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public Task<ManifestResult<ModsRegistry>> LoadRegistryAsync(bool forceRefresh, CancellationToken ct) =>
        LoadAsync<ModsRegistry>(ManifestBase, "mods.json", "mods.json", "PunkNexus.Fallback.mods.json", forceRefresh, ct);

    public Task<ManifestResult<ServersManifest>> LoadServersAsync(bool forceRefresh, CancellationToken ct) =>
        LoadAsync<ServersManifest>(ManifestBase, "servers.json", "servers.json", "PunkNexus.Fallback.servers.json", forceRefresh, ct);

    /// <summary>
    /// The virus scan reports for the whole catalog, in one request. One index rather than one
    /// file per mod: the list refreshes as a unit, and eighteen round trips to render a badge
    /// would make the Mods tab slower for something that is supporting evidence, not the point.
    ///
    /// No embedded fallback, unlike the catalog above. A scan report baked into the exe would age
    /// into a claim about files that are no longer the ones being downloaded, and a stale scan
    /// result is worse than none: "not scanned yet" is honest, a months-old verdict is not.
    /// </summary>
    public Task<ManifestResult<ScanIndex>> LoadScanIndexAsync(bool forceRefresh, CancellationToken ct) =>
        LoadAsync<ScanIndex>(ReportsBase, "index.json", "scan-index.json", null, forceRefresh, ct);

    private string ManifestBase => _settings.Current.ManifestBaseUrl.TrimEnd('/');

    /// <summary>
    /// Reports sit beside the manifest in the same repository, so their location is derived from
    /// the configured manifest URL rather than being a second setting to keep in step. Someone who
    /// points the client at their own catalog gets their own reports with it, automatically.
    /// </summary>
    private string ReportsBase
    {
        get
        {
            var manifest = ManifestBase.TrimEnd('/');

            // Replace the last PATH segment, never part of the scheme or host. Cutting at the last
            // '/' in the raw string did exactly that whenever the base had no path of its own:
            // "http://127.0.0.1:8931" cut at the slash inside "//" and became "http://reports",
            // where "reports" is the HOSTNAME. The lookup then failed with "No such host" and fell
            // back to the embedded copy, so a self-hosted catalog reported zero scans rather than
            // an error anyone could act on.
            if (!Uri.TryCreate(manifest, UriKind.Absolute, out var uri))
                return $"{manifest}/../reports";

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var path = segments.Length > 0
                ? string.Join('/', segments[..^1].Append("reports"))
                : "reports";

            return new UriBuilder(uri) { Path = path, Query = "", Fragment = "" }
                .Uri.ToString().TrimEnd('/');
        }
    }

    private async Task<ManifestResult<T>> LoadAsync<T>(
        string baseUrl, string fileName, string cacheName, string? embeddedName,
        bool forceRefresh, CancellationToken ct)
        where T : new()
    {
        var url = $"{baseUrl}/{fileName}";

        // Cached under its own name rather than the URL's: two documents in different folders can
        // share a file name, and reports/index.json is exactly that case.
        var cacheFile = Path.Combine(AppPaths.CacheDir, cacheName);

        // Cache-buster: raw.githubusercontent caches aggressively and a stale catalog looks like
        // a broken refresh button.
        var requestUrl = forceRefresh
            ? $"{url}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
            : url;

        try
        {
            var json = await _http.GetStringAsync(requestUrl, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<T>(json, Options)
                         ?? throw new InvalidDataException("The manifest was empty.");

            try
            {
                AppPaths.EnsureCreated();
                await File.WriteAllTextAsync(cacheFile, json, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not cache {fileName}: {ex.Message}");
            }

            return new ManifestResult<T>(parsed, ManifestOrigin.Network, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Could not fetch {fileName} from {url}: {ex.Message}");

            try
            {
                if (File.Exists(cacheFile))
                {
                    var json = await File.ReadAllTextAsync(cacheFile, ct).ConfigureAwait(false);
                    var parsed = JsonSerializer.Deserialize<T>(json, Options);
                    if (parsed is not null)
                        return new ManifestResult<T>(parsed, ManifestOrigin.Cache,
                            "Offline — showing the last downloaded catalog.");
                }
            }
            catch (Exception cacheEx)
            {
                Log.Warn($"Cached {fileName} is unreadable: {cacheEx.Message}");
            }

            if (embeddedName is null)
                return new ManifestResult<T>(new T(), ManifestOrigin.Embedded, null);

            var embedded = ReadEmbedded<T>(embeddedName);
            return new ManifestResult<T>(embedded ?? new T(), ManifestOrigin.Embedded,
                "Offline — showing the catalog built into this version.");
        }
    }

    private static T? ReadEmbedded<T>(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null) return default;
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read the built-in {resourceName}", ex);
            return default;
        }
    }
}
