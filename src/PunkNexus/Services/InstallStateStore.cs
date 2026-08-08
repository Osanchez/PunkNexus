using System.Text.Json;
using PunkNexus.Models;

namespace PunkNexus.Services;

/// <summary>Persists what this client wrote into each game folder, keyed by that folder's path.</summary>
public sealed class InstallStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public InstallState Load(string gameRoot)
    {
        var file = AppPaths.InstallStateFile(gameRoot);
        try
        {
            if (File.Exists(file))
            {
                var state = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(file));
                if (state is not null)
                {
                    state.Mods = new Dictionary<string, InstalledArtifact>(
                        state.Mods, StringComparer.OrdinalIgnoreCase);
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            // Losing the record costs precise uninstall, not correctness — the installer falls back
            // to removing the plugin folder.
            Log.Warn($"Install state for {gameRoot} is unreadable, treating as empty: {ex.Message}");
        }

        return new InstallState { GamePath = gameRoot };
    }

    public void Save(string gameRoot, InstallState state)
    {
        state.GamePath = gameRoot;
        try
        {
            AppPaths.EnsureCreated();
            var file = AppPaths.InstallStateFile(gameRoot);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save install state for {gameRoot}", ex);
        }
    }
}
