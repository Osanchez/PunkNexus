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
    /// A mismatch is reported, not enforced. Worth being precise about what this check is and is
    /// not: the checksum is published in the author's own manifest, in the same repository as the
    /// release it describes, so it cannot defend against an author whose account is compromised --
    /// whoever can swap the file can edit the checksum beside it. What it does catch is a download
    /// corrupted in transit, and a third party replacing a release asset without being able to
    /// edit the manifest.
    ///
    /// That is a narrow enough guarantee that refusing outright was the wrong trade: it stranded
    /// people on a mismatch they could not get past, for a signal that is often just a bad
    /// download. So the dialog says plainly what was found and lets the player decide, and the
    /// mismatch is logged either way.
    /// </summary>
    public bool ChecksumMismatch => Verdict == DownloadVerdict.Failed;

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
