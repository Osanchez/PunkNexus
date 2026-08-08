using System.Text.Json.Serialization;

namespace PunkNexus.Models;

// The catalog is three tiers, and the split is the whole point:
//
//   1. The REGISTRY (manifest/mods.json here) is a list of pointers. Developers PR into it once,
//      to get listed. It never carries a version number, so it cannot go stale.
//   2. The MOD MANIFEST (mod.json, in the developer's own repo) owns the version, the game version
//      it was built for, and where to download it. The developer bumps it as part of releasing.
//   3. The INSTALLED MANIFEST is that same mod.json, shipped inside the zip and landing in
//      BepInEx/plugins/<Mod>/. Reading it back is how the client knows what is actually installed.
//
// One document, authored once by the developer, serving as the published truth and the installed
// record. Nothing has to be kept in sync by hand, because nothing is written down twice.

/// <summary>Tier 1 — the registry. What a developer opens a pull request against.</summary>
public sealed class ModsRegistry
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("updatedUtc")] public string? UpdatedUtc { get; set; }

    /// <summary>
    /// The game build the catalog is curated against. Informational: the client gates on the
    /// version it detects in the user's own install, not on this.
    /// </summary>
    [JsonPropertyName("targetGameVersion")] public string? TargetGameVersion { get; set; }

    [JsonPropertyName("loader")] public LoaderEntry? Loader { get; set; }
    [JsonPropertyName("mods")] public List<RegistryEntry> Mods { get; set; } = new();
}

/// <summary>
/// A registry listing. Carries identity, presentation, and the pointer to the mod's own manifest —
/// deliberately not the version, the game version, or the download, all of which live in the mod
/// manifest so that releasing a mod never requires a registry pull request.
/// </summary>
public sealed class RegistryEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>Raw URL of the mod's own <c>mod.json</c>. The link that keeps versions in sync.</summary>
    [JsonPropertyName("manifestUrl")] public string ManifestUrl { get; set; } = "";

    // ---- presentation only: safe to be slightly stale, refreshed from the mod manifest on load
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
    [JsonPropertyName("homepage")] public string? Homepage { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();

    /// <summary>Lets the registry delist a mod without deleting its history.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

/// <summary>
/// Tiers 2 and 3 — the mod-owned manifest. Published at <see cref="RegistryEntry.ManifestUrl"/>
/// and shipped inside the zip so the installed copy is byte-identical to the published one.
/// </summary>
public sealed class ModManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string? Author { get; set; }

    /// <summary>The mod's current version. Authoritative — the registry never restates it.</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>
    /// The game version this build was made against. Compared for exact equality against the
    /// version detected in the user's install.
    /// </summary>
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";

    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
    [JsonPropertyName("homepage")] public string? Homepage { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();

    /// <summary>Folder created under <c>BepInEx/plugins/</c>. Defaults to the id.</summary>
    [JsonPropertyName("pluginFolder")] public string? PluginFolder { get; set; }

    /// <summary>Mod ids that must be installed alongside this one.</summary>
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = new();

    [JsonPropertyName("download")] public AssetSource? Download { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }

    public string EffectivePluginFolder =>
        string.IsNullOrWhiteSpace(PluginFolder) ? Id : PluginFolder!;

    /// <summary>The file name this manifest takes in the mod folder, published and installed alike.</summary>
    public const string FileName = "mod.json";
}

/// <summary>BepInEx itself — installed once, before any mod can load.</summary>
public sealed class LoaderEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "BepInEx";
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("source")] public AssetSource? Source { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("infoUrl")] public string? InfoUrl { get; set; }
}

/// <summary>
/// Where an artifact comes from. Either a fixed <see cref="Url"/>, or a repo plus a glob matched
/// against the latest release's asset names — release zips commonly embed their version in the
/// filename, and a fixed URL would 404 on the next version bump.
/// </summary>
public sealed class AssetSource
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("repo")] public string? Repo { get; set; }
    [JsonPropertyName("assetPattern")] public string? AssetPattern { get; set; }
}

/// <summary>The published server list.</summary>
public sealed class ServersManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("updatedUtc")] public string? UpdatedUtc { get; set; }
    [JsonPropertyName("servers")] public List<ServerEntry> Servers { get; set; } = new();
}

public sealed class ServerEntry
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }
    [JsonPropertyName("gameMode")] public string? GameMode { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("passworded")] public bool Passworded { get; set; }

    /// <summary>Mod ids the server runs; the client filters on these.</summary>
    [JsonPropertyName("mods")] public List<string> Mods { get; set; } = new();

    [JsonPropertyName("lastSeenUtc")] public string? LastSeenUtc { get; set; }

    public string Endpoint => string.IsNullOrWhiteSpace(Address) ? "" : $"{Address}:{Port}";
}
