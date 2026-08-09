namespace PunkNexus.Services;

public enum DownloadVerdict
{
    /// <summary>A checksum was published and the file matched it.</summary>
    Verified,

    /// <summary>The file downloaded, but nothing proves it is the file the author published.</summary>
    Unverified,

    /// <summary>The file does not match the checksum the manifest published.</summary>
    Failed,
}

/// <summary>
/// What was checked between "downloaded" and "written to the game folder". Shown to the user before
/// a single file is extracted, because that is the last moment where declining still costs nothing.
/// </summary>
public sealed record DownloadReport(
    string ModName,
    string FileName,
    long SizeBytes,
    DownloadVerdict Verdict,
    string Headline,
    string Message,
    IReadOnlyList<DialogDetail> Details)
{
    /// <summary>
    /// The file did not match a checksum its author published.
    ///
    /// This one is enforced: publishing a checksum is a promise about exactly which bytes the
    /// author released, and a file that fails it is not the released file. Not publishing one is
    /// no promise at all, and is only reported — so the block lands on a broken guarantee rather
    /// than on the mods that never made one.
    ///
    /// Worth being precise about the guarantee's limits: the checksum lives in the author's own
    /// manifest, in the same repository as the release, so it cannot defend against an author
    /// whose account is compromised -- whoever can swap the file can edit the checksum beside it.
    /// What it does catch is a download corrupted in transit, and a third party replacing a
    /// release asset without being able to edit the manifest.
    /// </summary>
    public bool ChecksumMismatch { get; init; }

    /// <summary>Whether the install is refused outright rather than put to the user.</summary>
    public bool Blocks => ChecksumMismatch;

    public DialogKind Kind => Verdict switch
    {
        DownloadVerdict.Verified => DialogKind.Success,
        DownloadVerdict.Unverified => DialogKind.Warning,
        _ => DialogKind.Danger,
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "unknown size",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
