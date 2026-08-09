namespace PunkNexus.Services;

/// <summary>
/// The lobby metadata contract, mirrored from PunkMultiverse's SteamLobbyController.
///
/// These strings are the wire format between the mod and this client. They are short because Steam
/// caps total lobby metadata, and they must not be renamed on one side alone — an older mod build
/// that still writes the old key simply stops appearing in the browser, with nothing to warn you.
/// </summary>
public static class SteamLobbyKeys
{
    // ---- already published by PunkMultiverse today
    public const string ModVersion = "pmvver";
    public const string GameVersion = "gamebuild";
    public const string HostId = "host";
    public const string Transport = "xport";     // "SteamServer" marks a discovery lobby
    public const string ServerId = "srvid";      // coordinator / listen-server SteamID64

    // ---- added for the server browser
    public const string Listed = "listed";       // "1" opts the lobby into public browsing
    public const string Name = "name";
    public const string GameMode = "mode";
    public const string Region = "region";
    public const string MaxPlayers = "maxp";

    /// <summary>
    /// The host's own count of occupied player slots. Published because Steam's
    /// <c>GetNumLobbyMembers</c> is only dependable for a lobby you are a member of, and a browser
    /// is by definition not a member of anything it is listing.
    /// </summary>
    public const string Players = "np";

    public const string Mods = "mods";           // comma-separated catalog ids, kept short on purpose

    /// <summary>
    /// "1" when a password is required. Reserved: PunkMultiverse has no password feature, so this
    /// is absent on every lobby today and every row reads as open.
    /// </summary>
    public const string Passworded = "pw";

    /// <summary>
    /// Value of <see cref="Listed"/> that opts a lobby in. Browsing filters on this, so a
    /// friends-only co-op session never shows up in a public list by accident.
    /// </summary>
    public const string ListedYes = "1";
}
