using System.Text.Json.Serialization;

namespace PunkNexus.Models;

public sealed class AppSettings
{
    [JsonPropertyName("gamePath")] public string? GamePath { get; set; }

    [JsonPropertyName("manifestBaseUrl")]
    public string ManifestBaseUrl { get; set; } = DefaultManifestBaseUrl;

    [JsonPropertyName("verifyChecksums")] public bool VerifyChecksums { get; set; } = true;

    public const string DefaultManifestBaseUrl =
        "https://raw.githubusercontent.com/Osanchez/PunkNexus/main/manifest";
}

/// <summary>
/// What this client installed into one specific game folder. Kept out of the game folder (it is
/// ours, not the game's) and keyed by that folder's path.
/// </summary>
public sealed class InstallState
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("gamePath")] public string? GamePath { get; set; }
    [JsonPropertyName("loader")] public InstalledArtifact? Loader { get; set; }

    [JsonPropertyName("mods")]
    public Dictionary<string, InstalledArtifact> Mods { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class InstalledArtifact
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("installedUtc")] public string? InstalledUtc { get; set; }

    /// <summary>
    /// Every file this client wrote, relative to the game folder and '/'-separated. Recorded so an
    /// uninstall removes exactly what was added and nothing else.
    /// </summary>
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
}
