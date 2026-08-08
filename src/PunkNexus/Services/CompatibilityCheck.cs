namespace PunkNexus.Services;

public enum CompatibilityState
{
    /// <summary>The mod declares exactly the game version installed.</summary>
    Compatible,

    /// <summary>The mod declares a different game version. Install is blocked.</summary>
    Incompatible,

    /// <summary>The mod declares nothing. A packaging error, and blocked as one.</summary>
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
    public bool Blocks => State is CompatibilityState.Incompatible or CompatibilityState.Undeclared;
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
                "This mod does not declare which game version it was built for, so it cannot be " +
                "installed. Its author needs to add a gameVersion to its manifest.");

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
            $"Built for game {declared}, but yours is {installed}.");
    }
}
