using PunkNexus.Models;

namespace PunkNexus.Services;

/// <summary>One thing the play flow has to do before the game can start.</summary>
public sealed class PlanStep
{
    public required string Summary { get; init; }

    /// <summary>True for something being added, false for something the client cannot do, null for
    /// a change that is neither — matches the dialog's pass/fail/neutral markers.</summary>
    public bool? Ok { get; init; }
}

/// <summary>
/// What joining a given server would change about the user's install. Built before anything is
/// touched, so the user is shown the consequences and consents to them rather than discovering
/// afterwards that their mod folder is different.
/// </summary>
public sealed class PlayPlan
{
    /// <summary>Registry entries that must be downloaded and installed.</summary>
    public List<RegistryEntry> Install { get; } = new();

    /// <summary>Plugin folders to move aside — mods the user has that this server does not run.</summary>
    public List<string> SetAside { get; } = new();

    /// <summary>Mods the server declares that are in no registry, so they cannot be installed.</summary>
    public List<string> Unresolvable { get; } = new();

    /// <summary>Mods the server runs that the user already has at the right version.</summary>
    public List<string> AlreadyPresent { get; } = new();

    /// <summary>True when the install already matches and the game can simply be launched.</summary>
    public bool IsReady => Install.Count == 0 && SetAside.Count == 0;

    /// <summary>
    /// True when the server declares mods this client cannot obtain. Joining is still allowed —
    /// the server's own handshake is the authority on whether the set is acceptable — but the user
    /// is told rather than sent into a rejection they cannot diagnose.
    /// </summary>
    public bool HasGaps => Unresolvable.Count > 0;

    public IEnumerable<PlanStep> Steps()
    {
        foreach (var mod in Install)
            yield return new PlanStep { Summary = $"Install {mod.Name}", Ok = true };

        foreach (var folder in SetAside)
            yield return new PlanStep { Summary = $"Set aside {folder} — restored when you finish" };

        foreach (var missing in Unresolvable)
            yield return new PlanStep { Summary = $"{missing} is not in the catalog — cannot install it", Ok = false };
    }
}

/// <summary>
/// Turns "join this server" into an install that matches it, and — the part that matters — turns it
/// back afterwards.
///
/// PunkMultiverse compares the joiner's ENTIRE BepInEx plugin set against the host's and, on the
/// default Reject policy, refuses any difference. So joining is not "add the server's mods", it is
/// "make my plugin folder equal the server's". That is destructive by nature, which is exactly why
/// nothing here deletes: everything in the way is shelved by <see cref="ModShelf"/> and restored.
/// </summary>
public sealed class PlayService
{
    private readonly InstallService _installer;
    private readonly ModManifestService _manifests;
    private readonly InstallStateStore _store;
    private readonly ModShelf _shelf = new();

    public PlayService(InstallService installer, ModManifestService manifests, InstallStateStore store)
    {
        _installer = installer;
        _manifests = manifests;
        _store = store;
    }

    // ---------------------------------------------------------------- planning

    /// <summary>
    /// Works out what would have to change for this install to match <paramref name="serverMods"/>.
    ///
    /// A server names its mods either by registry id or by BepInEx plugin GUID depending on the mod
    /// build, so both are matched. Nothing is written.
    /// </summary>
    public PlayPlan Plan(string gameRoot, IEnumerable<string> serverMods, IReadOnlyList<RegistryEntry> registry)
    {
        var plan = new PlayPlan();
        var wantedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var declared in serverMods.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()))
        {
            var entry = Resolve(declared, registry);
            if (entry is null)
            {
                plan.Unresolvable.Add(declared);
                continue;
            }

            var folder = FolderFor(entry);
            wantedFolders.Add(folder);

            if (InstallService.IsModInstalled(gameRoot, folder)) plan.AlreadyPresent.Add(entry.Name);
            else plan.Install.Add(entry);
        }

        // Everything present that the server does not run has to go — not because it is unwanted,
        // but because the host counts it as a difference and refuses the join.
        foreach (var folder in ModShelf.PresentFolders(gameRoot))
            if (!wantedFolders.Contains(folder))
                plan.SetAside.Add(folder);

        return plan;
    }

    /// <summary>Registry id first, then the BepInEx GUID a mod declares in its manifest.</summary>
    private static RegistryEntry? Resolve(string declared, IReadOnlyList<RegistryEntry> registry)
    {
        var byId = registry.FirstOrDefault(e =>
            string.Equals(e.Id, declared, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;

        return registry.FirstOrDefault(e =>
            !string.IsNullOrWhiteSpace(e.BepInExGuid) &&
            string.Equals(e.BepInExGuid, declared, StringComparison.OrdinalIgnoreCase));
    }

    private static string FolderFor(RegistryEntry entry) =>
        string.IsNullOrWhiteSpace(entry.PluginFolder) ? entry.Id : entry.PluginFolder!;

    // ---------------------------------------------------------------- applying

    /// <summary>
    /// Applies a plan. Shelving happens FIRST and is recorded before a single mod is downloaded, so
    /// an interrupted run leaves a state file that says exactly what is owed back — a crash between
    /// the two halves must not be the case where the user's mods are lost.
    /// </summary>
    public async Task ApplyAsync(
        string gameRoot, PlayPlan plan, string serverName,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var state = _store.Load(gameRoot);
        state.ActiveSwap ??= new ActiveSwap
        {
            ServerName = serverName,
            AppliedUtc = DateTime.UtcNow.ToString("o"),
        };

        foreach (var folder in plan.SetAside)
        {
            progress?.Report(new InstallProgress($"Setting aside {folder}"));
            var modId = state.Mods.FirstOrDefault(kv =>
                string.Equals(kv.Value.PluginFolder ?? kv.Key, folder, StringComparison.OrdinalIgnoreCase)).Key;

            var record = _shelf.Shelve(gameRoot, folder, modId);
            if (record is null) continue;

            state.Shelved.RemoveAll(s => string.Equals(s.Folder, folder, StringComparison.OrdinalIgnoreCase));
            state.Shelved.Add(record);
            _store.Save(gameRoot, state);       // persist per folder, not per batch
        }

        foreach (var entry in plan.Install)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new InstallProgress($"Fetching {entry.Name}"));

            var manifest = await _manifests.FetchAsync(entry, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                Log.Warn($"No manifest resolved for {entry.Id}; skipping it in the play setup.");
                continue;
            }

            if (!await _installer.InstallModAsync(gameRoot, manifest, progress, ct).ConfigureAwait(false))
                throw new InstallException($"Installing {entry.Name} was cancelled.");

            state = _store.Load(gameRoot);      // the installer just rewrote it
            state.ActiveSwap ??= new ActiveSwap { ServerName = serverName, AppliedUtc = DateTime.UtcNow.ToString("o") };
            if (!state.ActiveSwap.Added.Contains(entry.Id)) state.ActiveSwap.Added.Add(entry.Id);
            _store.Save(gameRoot, state);
        }

        Log.Info($"Play setup for \"{serverName}\": installed {plan.Install.Count}, shelved {plan.SetAside.Count}.");
    }

    // ---------------------------------------------------------------- restoring

    /// <summary>True when this install is currently carrying a temporary server mod set.</summary>
    public bool HasSwap(string gameRoot)
    {
        var state = _store.Load(gameRoot);
        return state.ActiveSwap is not null || state.Shelved.Count > 0;
    }

    public string? SwapServerName(string gameRoot) => _store.Load(gameRoot).ActiveSwap?.ServerName;

    /// <summary>
    /// Puts the user's own mods back: removes what the swap added, then unshelves everything it
    /// moved. Safe to call when nothing is owed, and safe to call twice.
    ///
    /// Each folder is cleared from the state record only once it is actually back, so a partial
    /// failure leaves the remainder owed rather than silently forgotten.
    /// </summary>
    public async Task<bool> RestoreAsync(string gameRoot, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var state = _store.Load(gameRoot);
        if (state.ActiveSwap is null && state.Shelved.Count == 0) return true;

        foreach (var id in state.ActiveSwap?.Added.ToList() ?? new List<string>())
        {
            ct.ThrowIfCancellationRequested();
            if (!state.Mods.TryGetValue(id, out var artifact)) continue;

            progress?.Report(new InstallProgress($"Removing {id}"));
            try
            {
                await _installer
                    .UninstallModAsync(gameRoot, id, artifact.PluginFolder ?? id, id, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A mod that will not uninstall is not a reason to abandon the restore — the
                // shelved folders are the user's property and matter more.
                Log.Warn($"Could not remove {id} while restoring: {ex.Message}");
            }
        }

        state = _store.Load(gameRoot);
        var complete = true;

        foreach (var shelved in state.Shelved.ToList())
        {
            progress?.Report(new InstallProgress($"Restoring {shelved.Folder}"));

            // Restored and NothingShelved both discharge the record; only a failed move stays owed.
            if (_shelf.Unshelve(gameRoot, shelved.Folder) == UnshelveResult.Failed)
                complete = false;
            else
                state.Shelved.RemoveAll(s => string.Equals(s.Folder, shelved.Folder, StringComparison.OrdinalIgnoreCase));

            _store.Save(gameRoot, state);
        }

        if (complete)
        {
            state.ActiveSwap = null;
            _store.Save(gameRoot, state);
            ModShelf.TidyEmptyShelf(gameRoot);
            Log.Info("Restored the user's own mod set.");
        }
        else
        {
            Log.Warn("Restore was incomplete; the remaining folders stay recorded and will be retried.");
        }

        return complete;
    }
}
