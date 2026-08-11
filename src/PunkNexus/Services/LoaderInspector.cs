using System.Diagnostics;
using PunkNexus.Models;

namespace PunkNexus.Services;

/// <summary>Which BepInEx is on disk. The generation matters more than the version number.</summary>
public enum LoaderGeneration
{
    /// <summary>No BepInEx at all.</summary>
    None,

    /// <summary>BepInEx 5 — <c>BepInEx.dll</c> and friends. Every mod in this catalog is built
    /// against 6 and cannot load here, which is why this is called out on its own.</summary>
    Legacy5,

    /// <summary>BepInEx 6 — <c>BepInEx.Core.dll</c>, <c>BepInEx.Unity.Mono.dll</c>.</summary>
    Six,

    /// <summary>A core folder exists but matches neither shape.</summary>
    Unknown,
}

/// <summary>What an inspection found, and what it means for installing mods.</summary>
public sealed record LoaderStatus(
    LoaderGeneration Generation,
    string? InstalledVersion,
    string? ExpectedVersion,
    IReadOnlyList<string> MissingCoreFiles,
    IReadOnlyList<string> LegacyFiles,
    bool InjectorPresent)
{
    public bool Installed => Generation is not LoaderGeneration.None;

    /// <summary>
    /// True when mods cannot be expected to load as things stand. Deliberately narrow: a version
    /// that merely differs from the catalog's is NOT broken, because a newer BepInEx than the one
    /// we pin is a perfectly good place to be and telling somebody to downgrade would be wrong.
    /// </summary>
    public bool IsBroken =>
        Generation is LoaderGeneration.Legacy5 or LoaderGeneration.Unknown
        || MissingCoreFiles.Count > 0
        || LegacyFiles.Count > 0
        || (Installed && !InjectorPresent);

    /// <summary>True when an update would change something. Version difference alone counts.</summary>
    public bool UpdateAvailable =>
        Installed && (IsBroken || (
            !string.IsNullOrWhiteSpace(ExpectedVersion) &&
            !string.Equals(InstalledVersion, ExpectedVersion, StringComparison.OrdinalIgnoreCase)));

    /// <summary>One line for the UI. Says what is wrong, not merely that something is.</summary>
    public string Headline => Generation switch
    {
        LoaderGeneration.None => "BepInEx is not installed.",
        LoaderGeneration.Legacy5 =>
            $"BepInEx 5 is installed ({InstalledVersion ?? "version unknown"}). Every mod listed here " +
            $"is built for BepInEx {ExpectedVersion ?? "6"} and will not load until it is updated.",
        LoaderGeneration.Unknown =>
            "The BepInEx folder does not look like a complete install of either BepInEx 5 or 6.",
        _ when LegacyFiles.Count > 0 =>
            $"BepInEx {InstalledVersion} is installed, but {LegacyFiles.Count} leftover BepInEx 5 " +
            "file(s) are mixed in with it. Mods can fail to load against a mixed install.",
        _ when MissingCoreFiles.Count > 0 =>
            $"BepInEx {InstalledVersion} is missing {MissingCoreFiles.Count} of its core file(s).",
        _ when !InjectorPresent =>
            "BepInEx's files are present but its loader is not, so the game starts without it. " +
            "This is usually a Steam file verification deleting winhttp.dll.",
        _ when UpdateAvailable =>
            $"BepInEx {InstalledVersion} is installed; {ExpectedVersion} is the version these mods " +
            "are built against.",
        _ => $"BepInEx {InstalledVersion}",
    };
}

/// <summary>
/// Reads the BepInEx install off disk and says whether mods can be expected to load against it.
///
/// This exists because of a real report that read as "the client is broken": mods sitting in the
/// right folder, and one plugin in the log. The cause is almost never the mod. It is a loader that
/// is the wrong GENERATION for it — BepInEx 5 renames every core assembly in 6
/// (<c>BepInEx.dll</c> vs <c>BepInEx.Core.dll</c>), so a plugin compiled against one is invisible
/// to the other, and BepInEx says nothing that names the mismatch.
///
/// The game is Unity 6000.3.4f1 on Mono, which BepInEx 5 predates. So a BepInEx 5 install here is
/// always wrong, however plausible it looked when someone downloaded it from bepinex.org.
/// </summary>
public static class LoaderInspector
{
    /// <summary>
    /// The core file set of a complete BepInEx 6 install, taken from the pinned setup archive.
    /// XML docs are excluded on purpose — they ship in the zip but nothing loads them, and calling
    /// an install broken over a missing documentation file would be a false alarm.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredCoreFiles = new[]
    {
        "0Harmony.dll",
        "AssetRipper.Primitives.dll",
        "BepInEx.Core.dll",
        "BepInEx.Preloader.Core.dll",
        "BepInEx.Unity.Common.dll",
        "BepInEx.Unity.Mono.Preloader.dll",
        "BepInEx.Unity.Mono.dll",
        "Mono.Cecil.Mdb.dll",
        "Mono.Cecil.Pdb.dll",
        "Mono.Cecil.Rocks.dll",
        "Mono.Cecil.dll",
        "MonoMod.RuntimeDetour.dll",
        "MonoMod.Utils.dll",
        "SemanticVersioning.dll",
    };

    /// <summary>BepInEx 5's core assemblies. None of these exist in 6; finding one means either a
    /// 5 install or a 6 install that was unzipped on top of one without clearing it out.</summary>
    private static readonly string[] LegacyCoreFiles =
    {
        "BepInEx.dll",
        "BepInEx.Preloader.dll",
        "BepInEx.Harmony.dll",
        "BepInEx.MonoMod.Loader.dll",
    };

    /// <summary>Sits next to the game exe, not under BepInEx. Without it nothing is injected at
    /// all and there is no log to read, which is its own confusing failure.</summary>
    private const string InjectorFile = "winhttp.dll";

    public static LoaderStatus Inspect(string? gameRoot, string? expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            return new LoaderStatus(LoaderGeneration.None, null, expectedVersion,
                Array.Empty<string>(), Array.Empty<string>(), false);

        var core = Path.Combine(gameRoot, "BepInEx", "core");
        bool injector = File.Exists(Path.Combine(gameRoot, InjectorFile));

        if (!Directory.Exists(core))
            return new LoaderStatus(LoaderGeneration.None, null, expectedVersion,
                Array.Empty<string>(), Array.Empty<string>(), injector);

        var legacy = LegacyCoreFiles
            .Where(f => File.Exists(Path.Combine(core, f)))
            .ToList();

        var sixMarker = Path.Combine(core, "BepInEx.Core.dll");
        if (!File.Exists(sixMarker))
        {
            // No 6 marker. Legacy files present means a genuine BepInEx 5; neither means something
            // we have no name for, and guessing at it would be worse than saying so.
            var generation = legacy.Count > 0 ? LoaderGeneration.Legacy5 : LoaderGeneration.Unknown;
            return new LoaderStatus(generation, ReadLegacyVersion(core), expectedVersion,
                Array.Empty<string>(), legacy, injector);
        }

        var missing = RequiredCoreFiles
            .Where(f => !File.Exists(Path.Combine(core, f)))
            .ToList();

        return new LoaderStatus(LoaderGeneration.Six, ReadVersion(sixMarker), expectedVersion,
            missing, legacy, injector);
    }

    /// <summary>
    /// The version BepInEx reports about itself. <c>ProductVersion</c> carries the informational
    /// version ("6.0.0-be.785+&lt;sha&gt;"), which is the only place the build number survives —
    /// <c>FileVersion</c> is a flat "6.0.0.0" on every bleeding-edge build alike, so it cannot tell
    /// two of them apart and is no use for deciding whether an update is available.
    /// </summary>
    private static string? ReadVersion(string dll)
    {
        try
        {
            var product = FileVersionInfo.GetVersionInfo(dll).ProductVersion;
            if (string.IsNullOrWhiteSpace(product)) return null;
            var plus = product.IndexOf('+');           // drop the commit hash
            return (plus > 0 ? product[..plus] : product).Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadLegacyVersion(string core)
    {
        var dll = Path.Combine(core, "BepInEx.dll");
        return File.Exists(dll) ? ReadVersion(dll) : null;
    }
}
