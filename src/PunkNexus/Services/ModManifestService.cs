using System.Text.Json;
using PunkNexus.Models;

namespace PunkNexus.Services;

/// <summary>
/// Fetches the mod-owned <c>mod.json</c> that each registry entry points at, and reads the copy
/// that shipped inside an installed mod. Both sides of "is this up to date" come from the same
/// document, which is what keeps the answer honest.
/// </summary>
public sealed class ModManifestService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly HttpClient _http;

    public ModManifestService(HttpClient http) => _http = http;

    /// <summary>
    /// Downloads a mod's published manifest. Returns null on any failure — the row then shows as
    /// unavailable rather than falling back to a stale version the registry happened to remember.
    /// </summary>
    public async Task<ModManifest?> FetchAsync(RegistryEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.ManifestUrl))
        {
            Log.Warn($"Registry entry '{entry.Id}' has no manifestUrl.");
            return null;
        }

        try
        {
            // raw.githubusercontent caches hard; without this a just-released version stays hidden.
            var url = entry.ManifestUrl.Contains('?')
                ? $"{entry.ManifestUrl}&t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
                : $"{entry.ManifestUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<ModManifest>(json, Options);

            if (manifest is null)
            {
                Log.Warn($"Manifest for '{entry.Id}' was empty.");
                return null;
            }

            // The registry says which mod this is; the manifest must agree, or one of the two is
            // pointing at the wrong thing and installing it would put unexpected files on disk.
            if (!string.IsNullOrWhiteSpace(manifest.Id) &&
                !string.Equals(manifest.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error(
                    $"Manifest at {entry.ManifestUrl} declares id '{manifest.Id}' but the registry " +
                    $"lists it as '{entry.Id}'. Ignoring it.");
                return null;
            }

            return manifest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Could not fetch the manifest for '{entry.Id}' from {entry.ManifestUrl}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Reads the manifest an installed mod shipped with, from its plugin folder.</summary>
    public static ModManifest? ReadInstalled(string gameRoot, string pluginFolder)
    {
        var path = Path.Combine(gameRoot, "BepInEx", "plugins", pluginFolder, ModManifest.FileName);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            Log.Warn($"Installed manifest {path} is unreadable: {ex.Message}");
            return null;
        }
    }
}
