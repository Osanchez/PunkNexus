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

    /// <summary>Raised right after a launch succeeds, so the UI can go "running" without waiting
    /// for the next poll to notice.</summary>
    public event Action? Started;

    private Process? _process;

    /// <summary>True while a game THIS launcher started is still running.</summary>
    public bool IsOursRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch { return false; }
        }
    }

    /// <summary>
    /// True while PUNK is running from <paramref name="gameRoot"/>, no matter who started it.
    ///
    /// Holding a <see cref="Process"/> handle only answers "did I start one and is it still alive",
    /// which is the wrong question for a button: the usual way to have PUNK open is to have started
    /// it from Steam, and a launcher that only knows about its own children reports "not running"
    /// for every one of those. So the running check is a lookup, not a handle.
    ///
    /// Matched on the executable's full path rather than the process name. Two installs of the same
    /// game -- a Steam copy and a test copy -- both run a process called "Punk", and disabling this
    /// install's button because a different install is open would be wrong. A process we cannot
    /// interrogate (another user's, or one exiting as we look) is treated as "not ours", because
    /// guessing would disable the button on evidence we do not have.
    /// </summary>
    public static bool IsRunningFrom(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return false;

        string exe;
        try { exe = Path.GetFullPath(Path.Combine(gameRoot, GameLocator.GameExe)); }
        catch { return false; }

        // Both spellings, because the platforms disagree about what a process is called: Windows
        // reports ProcessName with the extension stripped ("Punk"), Linux reports the comm value
        // verbatim ("Punk.exe"). Asking for only one silently finds nothing on the other platform,
        // which is a running game reported as not running -- and it is invisible until something
        // depends on the answer. The wrong-platform lookup simply returns an empty set.
        var names = new[] { Path.GetFileNameWithoutExtension(GameLocator.GameExe), GameLocator.GameExe }
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<Process>();
        foreach (var name in names)
        {
            try { candidates.AddRange(Process.GetProcessesByName(name)); }
            catch (Exception ex) { Log.Warn($"Could not enumerate '{name}' processes: {ex.Message}"); }
        }

        try
        {
            foreach (var p in candidates)
            {
                try
                {
                    var file = p.MainModule?.FileName;
                    if (file is not null &&
                        string.Equals(Path.GetFullPath(file), exe, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // Access denied, or it exited between the enumeration and this read.
                }
            }
        }
        finally
        {
            foreach (var p in candidates) p.Dispose();
        }

        return false;
    }

    /// <summary>Steam's own convention — what the overlay passes, and what the mod has always read.</summary>
    public static string ConnectLobbyArgs(string lobbyId) => $"+connect_lobby {Safe(lobbyId)}";

    /// <summary>
    /// A join target must be ONE argv entry. The value comes from a catalog, which is remote data,
    /// and it used to be interpolated straight into the command line -- so an entry whose address
    /// contained a space could append arguments of its own choosing to the game. That is not merely
    /// untidy: BepInEx takes --doorstop-target &lt;dll&gt; from the command line, which is a
    /// code-loading switch. Anything with whitespace or a leading dash is refused outright rather
    /// than escaped, because no legitimate host:port, SteamID64 or PMV- code contains either.
    /// </summary>
    private static string Safe(string target)
    {
        var value = (target ?? "").Trim();
        if (value.Length == 0)
            throw new InstallException("That server entry has no address to join.");
        if (value.Any(char.IsWhiteSpace) || value.StartsWith('-') || value.StartsWith('+'))
            throw new InstallException(
                $"That server entry's join target is not a plain address: '{value}'. "
                + "It was refused rather than passed to the game.");
        return value;
    }

    /// <summary>
    /// The mod's transport-agnostic join argument. Takes anything the in-game JOIN button takes —
    /// <c>host:port</c>, a dedicated server's SteamID64, or a <c>PMV-…</c> code — and the mod picks
    /// the transport from the target's shape. That is what lets a self-hosted UDP server be
    /// auto-joined without the player having to change their configured transport.
    /// </summary>
    public static string ConnectArgs(string target) => $"+punkmv_connect {Safe(target)}";

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

        // ArgumentList, not a single Arguments string: the runtime quotes each entry, so a value
        // can never split into several arguments no matter what it contains. Safe() above already
        // refuses the shapes that would try; this makes the attempt harmless as well as rejected.
        if (!string.IsNullOrWhiteSpace(arguments))
            foreach (var part in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                start.ArgumentList.Add(part);

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
            Started?.Invoke();
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
