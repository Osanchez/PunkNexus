using System.Text.Json.Serialization;

namespace PunkNexus.Models;

/// <summary>The mod catalogue, fetched from <c>manifest/mods.json</c> in the PunkNexus repo.</summary>
public sealed class ModsManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("updatedUtc")] public string? UpdatedUtc { get; set; }
    [JsonPropertyName("loader")] public LoaderEntry? Loader { get; set; }
    [JsonPropertyName("mods")] public List<ModEntry> Mods { get; set; } = new();
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
/// against the latest release's asset names — the mod zips embed their version in the filename,
/// so a fixed URL would 404 on the next version bump.
/// </summary>
public sealed class AssetSource
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("repo")] public string? Repo { get; set; }
    [JsonPropertyName("assetPattern")] public string? AssetPattern { get; set; }
}

public sealed class ModEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Folder created under <c>BepInEx/plugins/</c>. Used to detect a manual install.</summary>
    [JsonPropertyName("pluginFolder")] public string? PluginFolder { get; set; }

    [JsonPropertyName("source")] public AssetSource? Source { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
    [JsonPropertyName("homepage")] public string? Homepage { get; set; }
    [JsonPropertyName("gameVersion")] public string? GameVersion { get; set; }
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();

    /// <summary>Mod ids that must be installed alongside this one.</summary>
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = new();

    public string EffectivePluginFolder => string.IsNullOrWhiteSpace(PluginFolder) ? Id : PluginFolder!;
}

/// <summary>The server list, fetched from <c>manifest/servers.json</c>.</summary>
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
