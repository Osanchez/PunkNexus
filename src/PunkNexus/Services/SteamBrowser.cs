using System.Reflection;
using System.Runtime.InteropServices;
using PunkNexus.Models;
using Steamworks;

namespace PunkNexus.Services;

public enum SteamStatus
{
    /// <summary>Initialized and usable.</summary>
    Ready,

    /// <summary>Steam is not running, or the user is not logged in.</summary>
    NotRunning,

    /// <summary>The game's steam_api64.dll could not be located or loaded.</summary>
    NoLibrary,

    /// <summary>Only Windows builds ship the native library we bind to.</summary>
    Unsupported,

    /// <summary>Initialization threw. The reason is in <see cref="SteamBrowser.StatusDetail"/>.</summary>
    Failed,
}

/// <summary>
/// Browses PunkMultiverse sessions through Steam's lobby list.
///
/// Valve hosts the directory, so there is nothing to run and nothing to pay for, and a lobby is
/// destroyed when its last member leaves — which means the list cannot go stale the way a cached
/// heartbeat can. The client never contacts a game server to build this list.
///
/// Every entry point is failure-tolerant: Steam not running is the normal case for someone browsing
/// mods, not an error, and it must never take the window down.
/// </summary>
public sealed class SteamBrowser : IDisposable
{
    private const int LobbyDataTimeoutMs = 6000;

    private readonly object _gate = new();
    private bool _initialized;
    private bool _resolverInstalled;

    public SteamStatus Status { get; private set; } = SteamStatus.NotRunning;
    public string? StatusDetail { get; private set; }

    /// <summary>
    /// Brings Steam up, borrowing the native library from the verified game folder.
    ///
    /// The Steamworks NuGet package deliberately ships no steam_api64.dll — Valve's binary is not
    /// ours to redistribute, and this repo is public. The game install already has one, and the
    /// setup gate guarantees we know where that is, so we load theirs instead of shipping a copy.
    /// </summary>
    public bool TryInitialize(string gameRoot)
    {
        lock (_gate)
        {
            if (_initialized) return true;

            if (!OperatingSystem.IsWindows())
            {
                Status = SteamStatus.Unsupported;
                StatusDetail = "Steam browsing is only available on Windows builds.";
                return false;
            }

            var native = Path.Combine(gameRoot, "steam_api64.dll");
            if (!File.Exists(native))
            {
                Status = SteamStatus.NoLibrary;
                StatusDetail = $"steam_api64.dll was not found in {gameRoot}.";
                Log.Warn(StatusDetail);
                return false;
            }

            try
            {
                InstallResolver(native);

                // SteamAPI_Init looks for steam_appid.txt in the working directory and then falls
                // back to this variable. Setting it avoids writing a file next to a single-file exe,
                // whose directory is a temporary extraction path.
                Environment.SetEnvironmentVariable("SteamAppId", GameLocator.SteamAppId);
                Environment.SetEnvironmentVariable("SteamGameId", GameLocator.SteamAppId);

                if (!SteamAPI.Init())
                {
                    Status = SteamStatus.NotRunning;
                    StatusDetail = "Steam is not running, or you are not signed in.";
                    return false;
                }

                _initialized = true;
                Status = SteamStatus.Ready;
                StatusDetail = null;
                Log.Info("Steam initialized for lobby browsing.");
                return true;
            }
            catch (Exception ex)
            {
                Status = SteamStatus.Failed;
                StatusDetail = $"Steam could not start: {ex.Message}";
                Log.Error("Steam initialization failed", ex);
                return false;
            }
        }
    }

    /// <summary>Points Steamworks' P/Invokes at the game's copy of the native library.</summary>
    private void InstallResolver(string nativePath)
    {
        if (_resolverInstalled) return;

        NativeLibrary.SetDllImportResolver(typeof(SteamAPI).Assembly, (name, assembly, path) =>
            name is "steam_api64" or "steam_api64.dll" && NativeLibrary.TryLoad(nativePath, out var handle)
                ? handle
                : IntPtr.Zero);

        _resolverInstalled = true;
    }

    /// <summary>
    /// Asks Steam for every listed PunkMultiverse lobby and reads each one's metadata.
    ///
    /// Filtered on <see cref="SteamLobbyKeys.Listed"/> so friends-only co-op sessions never appear,
    /// and on available slots so a full lobby is not offered as joinable.
    /// </summary>
    public async Task<IReadOnlyList<ServerEntry>> BrowseAsync(CancellationToken ct)
    {
        if (!_initialized) return Array.Empty<ServerEntry>();

        var lobbies = await RequestLobbyListAsync(ct).ConfigureAwait(false);
        var servers = new List<ServerEntry>(lobbies.Count);

        foreach (var lobby in lobbies)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entry = ReadLobby(lobby);
                if (entry is not null) servers.Add(entry);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read lobby {lobby.m_SteamID}: {ex.Message}");
            }
        }

        Log.Info($"Steam returned {servers.Count} listed session(s).");
        return servers;
    }

    private Task<List<CSteamID>> RequestLobbyListAsync(CancellationToken ct)
    {
        var completion = new TaskCompletionSource<List<CSteamID>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CallResult<LobbyMatchList_t>? callResult = null;

        try
        {
            // Only lobbies that opted in, and only ones with room. Distance filter is widened to
            // worldwide: this community is small enough that a regional cut would hide most of it.
            SteamMatchmaking.AddRequestLobbyListStringFilter(
                SteamLobbyKeys.Listed, SteamLobbyKeys.ListedYes, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(
                ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListFilterSlotsAvailable(1);

            callResult = CallResult<LobbyMatchList_t>.Create((result, ioFailure) =>
            {
                if (ioFailure)
                {
                    completion.TrySetResult(new List<CSteamID>());
                    return;
                }

                var found = new List<CSteamID>((int)result.m_nLobbiesMatching);
                for (var i = 0; i < result.m_nLobbiesMatching; i++)
                    found.Add(SteamMatchmaking.GetLobbyByIndex(i));

                completion.TrySetResult(found);
            });

            callResult.Set(SteamMatchmaking.RequestLobbyList());
        }
        catch (Exception ex)
        {
            Log.Error("Requesting the Steam lobby list failed", ex);
            return Task.FromResult(new List<CSteamID>());
        }

        // Steamworks is callback-driven and needs pumping; nothing arrives without RunCallbacks.
        return PumpUntilAsync(completion, callResult, ct);
    }

    private static async Task<List<CSteamID>> PumpUntilAsync(
        TaskCompletionSource<List<CSteamID>> completion, CallResult<LobbyMatchList_t>? callResult, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(LobbyDataTimeoutMs);

        try
        {
            while (!completion.Task.IsCompleted)
            {
                if (ct.IsCancellationRequested || DateTime.UtcNow > deadline)
                {
                    Log.Warn("Steam lobby list timed out.");
                    return new List<CSteamID>();
                }

                SteamAPI.RunCallbacks();
                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new List<CSteamID>();
        }
        finally
        {
            callResult?.Dispose();
        }
    }

    /// <summary>Turns one lobby's metadata into a row, or null when it is not a session we can join.</summary>
    private static ServerEntry? ReadLobby(CSteamID lobby)
    {
        string Data(string key) => SteamMatchmaking.GetLobbyData(lobby, key) ?? "";

        // A lobby with no server id is a plain co-op lobby: joinable through Steam, but it has no
        // address for this client to show, so it is not a "server" in the browser sense.
        var serverId = Data(SteamLobbyKeys.ServerId);
        if (string.IsNullOrWhiteSpace(serverId)) serverId = Data(SteamLobbyKeys.HostId);
        if (string.IsNullOrWhiteSpace(serverId)) return null;

        var name = Data(SteamLobbyKeys.Name);
        if (string.IsNullOrWhiteSpace(name)) name = $"PUNK session {serverId[..Math.Min(6, serverId.Length)]}";

        // Prefer what the host published over what Steam reports. GetNumLobbyMembers is only
        // dependable inside a lobby you have joined, and joining every row to count it would be
        // both slow and rude — so the host counts its own slots and says so.
        var players = SteamMatchmaking.GetNumLobbyMembers(lobby);
        if (int.TryParse(Data(SteamLobbyKeys.Players), out var declaredPlayers) && declaredPlayers >= 0)
            players = declaredPlayers;

        var maxPlayers = SteamMatchmaking.GetLobbyMemberLimit(lobby);
        if (int.TryParse(Data(SteamLobbyKeys.MaxPlayers), out var declaredMax) && declaredMax > 0)
            maxPlayers = declaredMax;

        var mods = Data(SteamLobbyKeys.Mods)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return new ServerEntry
        {
            Id = lobby.m_SteamID.ToString(),
            Source = ServerSource.Steam,
            Name = name,
            SteamId = serverId,
            Players = players,
            MaxPlayers = maxPlayers,
            GameMode = Data(SteamLobbyKeys.GameMode),
            Region = Data(SteamLobbyKeys.Region),
            Version = Data(SteamLobbyKeys.ModVersion),
            GameVersion = Data(SteamLobbyKeys.GameVersion),
            Passworded = Data(SteamLobbyKeys.Passworded) == "1",
            Mods = mods,
            // Steam destroys a lobby when its last member leaves, so anything we can see right now
            // is live by construction. No timestamp to reason about.
            LastSeenUtc = DateTime.UtcNow.ToString("o"),
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_initialized) return;
            try { SteamAPI.Shutdown(); }
            catch (Exception ex) { Log.Warn($"Steam shutdown failed: {ex.Message}"); }
            _initialized = false;
        }
    }
}
