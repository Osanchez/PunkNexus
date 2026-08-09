using PunkNexus.Models;

namespace PunkNexus.Services;

/// <summary>Outcome of putting one folder back. "Nothing was shelved" is a settled state, not a
/// failure — only <see cref="Failed"/> is owed a retry.</summary>
public enum UnshelveResult { Restored, NothingShelved, Failed }

/// <summary>
/// Where plugin folders go while a server's mod set is loaded, and how they come back.
///
/// The rule that makes this safe: <b>nothing is ever deleted to make room</b>. A folder that is in
/// the way is MOVED aside and moved back afterwards, byte for byte. Deleting and reinstalling would
/// be wrong twice over — the client cannot re-download a mod the user installed by hand, and a mod's
/// tuned settings commonly live inside its own plugin folder (PunkMultiverse keeps its config.cfg
/// there), so a reinstall would silently reset them.
///
/// The shelf lives inside the game's BepInEx folder rather than in the client's own storage,
/// because it must be on the SAME VOLUME as the plugins folder. A game on D:\ with the client's
/// data on C:\ would turn every move into a slow cross-volume copy that can fail half-finished; a
/// same-volume move is instant and either happened or did not. It sits beside <c>plugins</c>, not
/// inside it, so BepInEx never loads anything parked here.
/// </summary>
public sealed class ModShelf
{
    public static string PluginsDir(string gameRoot) =>
        Path.Combine(gameRoot, "BepInEx", "plugins");

    public static string ShelfDir(string gameRoot) =>
        Path.Combine(gameRoot, "BepInEx", "nexus-shelf");

    /// <summary>Plugin folder names currently present, ignoring the shelf itself.</summary>
    public static IReadOnlyList<string> PresentFolders(string gameRoot)
    {
        var plugins = PluginsDir(gameRoot);
        if (!Directory.Exists(plugins)) return Array.Empty<string>();

        try
        {
            return Directory.GetDirectories(plugins)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list plugin folders in {plugins}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Move one plugin folder out of the way. Returns the record to persist, or null when there
    /// was nothing there to move.
    /// </summary>
    public ShelvedFolder? Shelve(string gameRoot, string folder, string? modId)
    {
        var source = Path.Combine(PluginsDir(gameRoot), folder);
        if (!Directory.Exists(source)) return null;

        var shelf = ShelfDir(gameRoot);
        Directory.CreateDirectory(shelf);
        var target = Path.Combine(shelf, folder);

        // A leftover from an interrupted run would block the move. It is stale by definition —
        // the live folder is the one in plugins — so it goes.
        if (Directory.Exists(target)) DeleteTree(target);

        try
        {
            Directory.Move(source, target);
        }
        catch (Exception ex)
        {
            throw new InstallException(
                $"Could not set aside \"{folder}\": {ex.Message} " +
                "Close the game if it is running and try again.", ex);
        }

        Log.Info($"Shelved plugin folder \"{folder}\".");
        return new ShelvedFolder
        {
            Folder = folder,
            ModId = modId,
            ShelvedUtc = DateTime.UtcNow.ToString("o"),
        };
    }

    /// <summary>
    /// Put one folder back. Anything occupying its place is removed first — that occupant is the
    /// temporary copy the swap installed, and the user's own folder is the one that wins.
    /// </summary>
    public UnshelveResult Unshelve(string gameRoot, string folder)
    {
        var source = Path.Combine(ShelfDir(gameRoot), folder);

        // Nothing on the shelf is NOT a failure, and the difference matters: a failed move has to
        // stay on the books and be retried, while an absent one is already settled. Conflating them
        // leaves a record that can never be discharged, and the client nags about mods it has
        // already given back.
        if (!Directory.Exists(source)) return UnshelveResult.NothingShelved;

        var plugins = PluginsDir(gameRoot);
        Directory.CreateDirectory(plugins);
        var target = Path.Combine(plugins, folder);

        try
        {
            if (Directory.Exists(target)) DeleteTree(target);
            Directory.Move(source, target);
        }
        catch (Exception ex)
        {
            // Restoring must never throw away what it failed to move — leaving it on the shelf
            // keeps the files, and the state record keeps the next run trying.
            Log.Error($"Could not restore \"{folder}\" from the shelf", ex);
            return UnshelveResult.Failed;
        }

        Log.Info($"Restored plugin folder \"{folder}\" from the shelf.");
        return UnshelveResult.Restored;
    }

    /// <summary>Removes the shelf directory once it is empty, so a clean install stays clean.</summary>
    public static void TidyEmptyShelf(string gameRoot)
    {
        var shelf = ShelfDir(gameRoot);
        try
        {
            if (Directory.Exists(shelf) && !Directory.EnumerateFileSystemEntries(shelf).Any())
                Directory.Delete(shelf);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove the empty shelf: {ex.Message}");
        }
    }

    private static void DeleteTree(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception ex) { Log.Warn($"Could not delete {path}: {ex.Message}"); }
    }
}
