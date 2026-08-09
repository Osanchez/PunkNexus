using System.Diagnostics;

namespace PunkNexus.Services;

/// <summary>
/// Starts PUNK, optionally aimed at a lobby, and reports when it exits.
///
/// The exe is launched directly rather than through a <c>steam://</c> URL, and that is deliberate:
/// arguments are the whole point here, and Steam's URL handler is an unreliable way to pass them.
/// Launching directly makes <c>+connect_lobby &lt;id&gt;</c> arrive verbatim, which is what
/// PunkMultiverse's <c>ParseLaunchArgs</c> reads on a cold start to auto-join.
///
/// This costs nothing on the Steam side: the mod's SteamBootstrap already self-initializes the
/// Steam API on a direct launch, so a direct-launched game still has a Steam identity and the Steam
/// transport still works — the Steam client only has to be running.
///
/// Knowing when the game EXITS is not a nicety. It is the trigger that gives the user their own
/// mods back, so the process handle is held for exactly that reason.
/// </summary>
public sealed class GameLauncher
{
    /// <summary>Raised on a background thread when a game this launcher started has exited.</summary>
    public event Action? Exited;

    private Process? _process;

    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch { return false; }
        }
    }

    /// <summary>The launch argument that makes the mod join a Steam lobby on boot.</summary>
    public static string ConnectLobbyArgs(string lobbyId) => $"+connect_lobby {lobbyId}";

    /// <summary>
    /// Starts the game. <paramref name="arguments"/> may be null for a plain launch.
    /// Throws <see cref="InstallException"/> with something the user can act on.
    /// </summary>
    public void Launch(string gameRoot, string? arguments = null)
    {
        var exe = Path.Combine(gameRoot, GameLocator.GameExe);
        if (!File.Exists(exe))
            throw new InstallException(
                $"{GameLocator.GameExe} is not in {gameRoot}. Re-run setup and pick the game folder again.");

        var start = new ProcessStartInfo
        {
            FileName = exe,
            // The working directory has to be the game folder: BepInEx resolves its own paths and
            // the game resolves Punk_Data relative to it, so launching from elsewhere loads neither.
            WorkingDirectory = gameRoot,
            UseShellExecute = false,
        };

        if (!string.IsNullOrWhiteSpace(arguments)) start.Arguments = arguments;

        try
        {
            var process = Process.Start(start)
                ?? throw new InstallException("The game did not start, and Windows gave no reason.");

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                Log.Info("PUNK exited.");
                Exited?.Invoke();
            };

            _process = process;
            Log.Info($"Launched {exe}" + (string.IsNullOrWhiteSpace(arguments) ? "" : $" {arguments}"));
        }
        catch (InstallException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InstallException($"Could not start the game: {ex.Message}", ex);
        }
    }
}
