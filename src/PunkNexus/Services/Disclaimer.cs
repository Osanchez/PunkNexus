namespace PunkNexus.Services;

/// <summary>
/// The risk warning shown before the client can be used. Kept in one place so the text and the
/// version that records acceptance cannot drift apart.
/// </summary>
public static class Disclaimer
{
    /// <summary>
    /// Bump only when the warning changes in substance. Everyone is asked again when it does —
    /// consent to the old wording is not consent to new terms.
    /// </summary>
    public const int Version = 1;

    public static DialogRequest Request { get; } = new()
    {
        Title = "Before you install anything",
        Kind = DialogKind.Warning,
        Message =
            "PUNK Nexus makes installing mods easier. It does not make them safe.\n\n" +
            "Mods are written by third parties and run code inside your game. Installing one can " +
            "alter or delete files in your game folder, break saves, or stop the game launching.\n\n" +
            "You use this client at your own risk. Any damage to your installation or loss of data " +
            "is your responsibility — not this client's, and not the mod authors'.",
        Details = new[]
        {
            new DialogDetail("Back up your saves before installing anything you don't recognize."),
            new DialogDetail("Steam's “Verify integrity of game files” restores a broken install."),
        },
        AcceptText = "I understand — continue",
        DeclineText = "Quit",
    };
}
