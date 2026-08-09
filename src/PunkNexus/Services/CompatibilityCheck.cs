namespace PunkNexus.Services;

public enum CompatibilityState
{
    /// <summary>The mod declares exactly the game version installed.</summary>
    Compatible,

    /// <summary>The mod declares a different game version than the one installed.</summary>
    Incompatible,

    /// <summary>The mod declares nothing. A packaging error, but not a reason to refuse.</summary>
    Undeclared,

    /// <summary>The client could not read the game's version. Not the mod's fault; not blocked.</summary>
    UnknownGame,
}

public sealed record CompatibilityResult(
    CompatibilityState State,
    string? ModGameVersion,
    string? InstalledGameVersion,
    string Summary)
{
    /// <summary>
    /// True when we cannot confirm this mod matches the installed game. It is a WARNING, never a
    /// refusal.
    ///
    /// This used to block the install outright, on the theory that an author must publish a build
    /// per game version. In practice one base-game patch then took the whole catalogue down at
    /// once -- 0.12.10 to 0.12.11 left every one of the 16 listed mods uninstallable, each blaming
    /// its author for not having published something. Most mods are unaffected by a patch, the
    /// declared version is only ever the author's last claim rather than a tested fact, and a
    /// player who wants to try one is better served by a clear warning than by a dead button they
    /// cannot reason about.
    /// </summary>
    public bool IsWarning => State is not CompatibilityState.Compatible;
}

/// <summary>
/// Exact-match game-version gating.
///
/// The asymmetry here is deliberate. A mod that declares nothing, or declares the wrong version, is
/// blocked — that is the contract mod authors are held to. But when the *client* cannot read the
/// game's version, nothing is blocked: our own detection failing is not evidence against the mod,
/// and refusing to install anything would make the app useless on an install we simply cannot
/// fingerprint.
/// </summary>
public static class CompatibilityCheck
{
    public static CompatibilityResult Evaluate(string? modGameVersion, GameBuild build)
    {
        var declared = modGameVersion?.Trim();
        var installed = build.Version?.Trim();

        if (string.IsNullOrWhiteSpace(declared))
            return new CompatibilityResult(
                CompatibilityState.Undeclared, null, installed,
                "This mod does not declare which game version it was built for, so its " +
                "compatibility is unknown. Its author needs to add a gameVersion to its manifest.");

        if (string.IsNullOrWhiteSpace(installed))
            return new CompatibilityResult(
                CompatibilityState.UnknownGame, declared, null,
                $"Built for game {declared}. Your game's version could not be read, so this was " +
                "not checked.");

        if (string.Equals(declared, installed, StringComparison.OrdinalIgnoreCase))
            return new CompatibilityResult(
                CompatibilityState.Compatible, declared, installed, $"Built for game {declared}.");

        return new CompatibilityResult(
            CompatibilityState.Incompatible, declared, installed,
            $"Built for game {declared} and not reported as updated for {installed}. It may work "
            + "anyway — most mods are unaffected by a patch — but it has not been confirmed.");
    }
}
