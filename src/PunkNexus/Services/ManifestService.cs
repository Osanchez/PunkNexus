using System.Reflection;
using System.Text.Json;
using PunkNexus.Models;

namespace PunkNexus.Services;

public enum ManifestOrigin { Network, Cache, Embedded }

public sealed record ManifestResult<T>(T Value, ManifestOrigin Origin, string? Warning);

/// <summary>
/// Loads the catalogue with three fallbacks: the network, then the last good copy on disk, then the
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

    public Task<ManifestResult<ModsManifest>> LoadModsAsync(bool forceRefresh, CancellationToken ct) =>
        LoadAsync<ModsManifest>("mods.json", "PunkNexus.Fallback.mods.json", forceRefresh, ct);

    public Task<ManifestResult<ServersManifest>> LoadServersAsync(bool forceRefresh, CancellationToken ct) =>
        LoadAsync<ServersManifest>("servers.json", "PunkNexus.Fallback.servers.json", forceRefresh, ct);

    private async Task<ManifestResult<T>> LoadAsync<T>(
        string fileName, string embeddedName, bool forceRefresh, CancellationToken ct)
        where T : new()
    {
        var baseUrl = _settings.Current.ManifestBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/{fileName}";
        var cacheFile = Path.Combine(AppPaths.CacheDir, fileName);

        // Cache-buster: raw.githubusercontent caches aggressively and a stale catalogue looks like
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
                            "Offline — showing the last downloaded catalogue.");
                }
            }
            catch (Exception cacheEx)
            {
                Log.Warn($"Cached {fileName} is unreadable: {cacheEx.Message}");
            }

            var embedded = ReadEmbedded<T>(embeddedName);
            return new ManifestResult<T>(embedded ?? new T(), ManifestOrigin.Embedded,
                "Offline — showing the catalogue built into this version.");
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
