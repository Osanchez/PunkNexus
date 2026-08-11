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

    /// <summary>
    /// The mod's BepInEx plugin GUID. Optional, and only used to recognize a mod a SERVER names:
    /// a session advertises its plugin set, and depending on the mod build it names them by GUID
    /// rather than by registry id. Without this the client cannot tell that
    /// "com.example.punkloot" and the "PunkLoot" listing are the same mod.
    /// </summary>
    [JsonPropertyName("bepInExGuid")] public string? BepInExGuid { get; set; }

    /// <summary>Folder under <c>BepInEx/plugins/</c>. Mirrors the mod manifest; defaults to the id.</summary>
    [JsonPropertyName("pluginFolder")] public string? PluginFolder { get; set; }

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

    /// <summary>
    /// Where this mod has to be installed to do its job: <c>client</c>, <c>server</c> or
    /// <c>both</c>. Optional, and absent means the author has not said — which is reported as
    /// "not stated" rather than guessed at, because guessing wrong sends someone to install a
    /// server-side mod on their client and wonder why nothing happens.
    /// </summary>
    [JsonPropertyName("side")] public string? Side { get; set; }

    /// <summary>Folder created under <c>BepInEx/plugins/</c>. Defaults to the id.</summary>
    [JsonPropertyName("pluginFolder")] public string? PluginFolder { get; set; }

    /// <summary>Mod ids that must be installed alongside this one.</summary>
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = new();

    [JsonPropertyName("download")] public AssetSource? Download { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }

    public string EffectivePluginFolder =>
        string.IsNullOrWhiteSpace(PluginFolder) ? Id : PluginFolder!;

    /// <summary>
    /// True when a plugin folder is a single folder NAME rather than a path.
    ///
    /// This value arrives in a mod's own <c>mod.json</c>, fetched at runtime from the author's
    /// repository, and the client turns it into a real directory in two places that matter: it is
    /// the confinement boundary every extracted entry is checked against, and it is the directory
    /// uninstall removes recursively. A value like <c>../../Punk_Data</c> resolves to a real folder
    /// INSIDE the game directory, so the game-root guards on both paths still pass — and both are
    /// then pointed at the game's own files instead of at the mod's.
    ///
    /// <c>tools/validate-manifest.py</c> has always rejected this, but it runs on pull requests
    /// against the catalog in THIS repository, while the manifest it validated lives in the
    /// author's repository and can change afterwards with nothing re-checking it. A compromised or
    /// substituted listed mod is the exact case the checksum gate and the scan reports exist for,
    /// so the same rule has to hold on the client, at install time, against the bytes fetched.
    /// </summary>
    public static bool IsSafePluginFolderName(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        if (folder.Contains('/') || folder.Contains('\\')) return false;
        if (folder.Contains("..")) return false;
        // "C:", "C:x" and a leading separator all escape once combined.
        if (folder.Contains(':') || Path.IsPathRooted(folder)) return false;
        // Path.Combine would collapse these onto the plugins directory itself.
        if (folder is "." or "..") return false;
        return folder.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

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

/// <summary>Where a server row came from. Drives the Steam / Self-hosted filter.</summary>
public enum ServerSource
{
    /// <summary>Discovered through Steam's lobby list. Live by construction.</summary>
    Steam,

    /// <summary>A dedicated UDP server from the published list. See docs/SERVER_LIST.md.</summary>
    Dedicated,
}

public sealed class ServerEntry
{
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("source")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ServerSource Source { get; set; } = ServerSource.Dedicated;

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    // A row carries whichever address its transport uses: dedicated servers an address:port that
    // doubles as the join code, Steam sessions a SteamID64 with no routable address at all.
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("steamId")] public string? SteamId { get; set; }

    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }
    [JsonPropertyName("gameMode")] public string? GameMode { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("gameVersion")] public string? GameVersion { get; set; }
    [JsonPropertyName("passworded")] public bool Passworded { get; set; }

    /// <summary>Mod ids the server runs; the client filters on these.</summary>
    [JsonPropertyName("mods")] public List<string> Mods { get; set; } = new();

    [JsonPropertyName("lastSeenUtc")] public string? LastSeenUtc { get; set; }

    /// <summary>
    /// Estimated round-trip latency in milliseconds, or null when it could not be worked out —
    /// the host published no location, Steam has not finished measuring locally, or the route is
    /// one Valve cannot estimate. Null renders as "—" rather than as a misleading zero.
    ///
    /// Deliberately NOT deserialized from the published list. Latency is a property of the pair
    /// (this machine, that host), so a static document shared by every user cannot know it — a
    /// number from there would be somebody else's ping wearing yours. It is only ever computed
    /// locally, which today means Steam sessions; see docs/SERVER_LIST.md for the UDP case.
    /// </summary>
    [JsonIgnore] public int? PingMs { get; set; }

    public string PingText => PingMs is int ms ? $"{ms} ms" : "—";

    // Thresholds are the ones a player feels rather than anything measured: under ~60ms plays
    // local, under ~130ms plays fine, past that aiming starts to suffer.
    public bool PingIsGood => PingMs is int g && g < 60;
    public bool PingIsFair => PingMs is int f && f >= 60 && f < 130;
    public bool PingIsPoor => PingMs is int p && p >= 130;

    public string RegionText => string.IsNullOrWhiteSpace(Region) ? "—" : Region!;

    public bool IsSteam => Source == ServerSource.Steam;

    /// <summary>What the user sees in the address column, and for UDP what they can type to join.</summary>
    public string Endpoint =>
        !string.IsNullOrWhiteSpace(Address) ? $"{Address}:{Port}"
        : !string.IsNullOrWhiteSpace(SteamId) ? "Steam"
        : "";

    public string SourceLabel => Source == ServerSource.Steam ? "Steam" : "Self-hosted";
}

/// <summary>
/// Where a mod has to be installed for it to work.
///
/// A controlled vocabulary rather than free text, because this drives a filter — and the region
/// field on the server browser is the cautionary tale: free text nobody agrees on produces a filter
/// with nothing filterable in it. Three values cover every mod in practice, and an author who omits
/// it gets <see cref="Unstated"/>, which is a fact about the manifest and not a claim about the mod.
/// </summary>
public enum ModSide
{
    /// <summary>The author did not say, or said something this client does not recognize.</summary>
    Unstated,

    /// <summary>Only the player's own game needs it — HUD, input, cosmetics.</summary>
    Client,

    /// <summary>Only the host needs it. Installing it on a joining client does nothing.</summary>
    Server,

    /// <summary>Every participant needs it, host and joiners alike.</summary>
    Both,
}

public static class ModSides
{
    /// <summary>The wire values, which are what an author writes in mod.json.</summary>
    public const string ClientValue = "client";
    public const string ServerValue = "server";
    public const string BothValue = "both";

    /// <summary>
    /// Reads the declared value, tolerantly. An unrecognized string becomes
    /// <see cref="ModSide.Unstated"/> rather than an error: a manifest is fetched live from an
    /// author's repository and can say anything, and a value this build does not understand is not
    /// a reason to refuse to list the mod.
    /// </summary>
    public static ModSide Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        ClientValue => ModSide.Client,
        ServerValue => ModSide.Server,
        BothValue or "clientserver" or "client/server" => ModSide.Both,
        _ => ModSide.Unstated,
    };

    /// <summary>Badge text. Lowercase to sit with the other pills on a row.</summary>
    public static string Badge(ModSide side) => side switch
    {
        ModSide.Client => "client",
        ModSide.Server => "server",
        ModSide.Both => "client & server",
        _ => "",
    };

    /// <summary>The sentence shown on hover, which is where the actual advice belongs.</summary>
    public static string Explain(ModSide side) => side switch
    {
        ModSide.Client => "Install on your own game. A server does not need it.",
        ModSide.Server => "Install on the host or dedicated server. Installing it on a joining "
                        + "client does nothing.",
        ModSide.Both => "Everyone playing together needs this, host and joiners alike.",
        _ => "This mod's author has not stated where it needs to be installed.",
    };
}
