using System.Text.Json.Serialization;

namespace PunkNexus.Models;

public sealed class AppSettings
{
    [JsonPropertyName("gamePath")] public string? GamePath { get; set; }

    [JsonPropertyName("manifestBaseUrl")]
    public string ManifestBaseUrl { get; set; } = DefaultManifestBaseUrl;

    [JsonPropertyName("verifyChecksums")] public bool VerifyChecksums { get; set; } = true;

    /// <summary>
    /// Version of the risk disclaimer the user accepted. Stored as a version rather than a bool so
    /// that materially rewording the warning asks again instead of silently assuming consent.
    /// </summary>
    [JsonPropertyName("disclaimerAcceptedVersion")] public int DisclaimerAcceptedVersion { get; set; }

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

    /// <summary>
    /// Plugin folders currently moved out of <c>BepInEx/plugins</c> to make room for a server's mod
    /// set. Never empty while a swap is in effect, and the record survives a crash — it is what
    /// lets the next launch put the user's mods back without being told anything.
    /// </summary>
    [JsonPropertyName("shelved")] public List<ShelvedFolder> Shelved { get; set; } = new();

    /// <summary>The swap in effect, or null when the plugins folder is the user's own set.</summary>
    [JsonPropertyName("activeSwap")] public ActiveSwap? ActiveSwap { get; set; }
}

/// <summary>
/// One plugin folder parked on the shelf, and everything needed to put it back exactly as it was.
/// Folders are MOVED, never deleted and re-downloaded — a hand-installed mod has no download to
/// repeat, and a mod's tuned config.cfg commonly lives inside its own plugin folder.
/// </summary>
public sealed class ShelvedFolder
{
    /// <summary>Folder name under <c>BepInEx/plugins</c>, which is also its name on the shelf.</summary>
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";

    /// <summary>Registry id when this folder was something the client installed; null when the
    /// user put it there by hand and the client only knows the folder name.</summary>
    [JsonPropertyName("modId")] public string? ModId { get; set; }

    [JsonPropertyName("shelvedUtc")] public string? ShelvedUtc { get; set; }
}

/// <summary>A temporary mod set applied so the user could join a specific server.</summary>
public sealed class ActiveSwap
{
    /// <summary>Display name of what was joined, for the "your mods are set aside" banner.</summary>
    [JsonPropertyName("serverName")] public string? ServerName { get; set; }

    [JsonPropertyName("appliedUtc")] public string? AppliedUtc { get; set; }

    /// <summary>Mod ids the swap installed that were not present before, so a restore can take
    /// them back out again instead of leaving the user's folder quietly growing.</summary>
    [JsonPropertyName("added")] public List<string> Added { get; set; } = new();
}

public sealed class InstalledArtifact
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("installedUtc")] public string? InstalledUtc { get; set; }

    /// <summary>Folder this mod occupies under <c>BepInEx/plugins</c>. Recorded so a shelved folder
    /// can be traced back to the mod that owns it without re-reading every manifest.</summary>
    [JsonPropertyName("pluginFolder")] public string? PluginFolder { get; set; }

    /// <summary>
    /// Every file this client wrote, relative to the game folder and '/'-separated. Recorded so an
    /// uninstall removes exactly what was added and nothing else.
    /// </summary>
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
}
