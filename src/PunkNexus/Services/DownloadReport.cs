namespace PunkNexus.Services;

public enum DownloadVerdict
{
    /// <summary>A checksum was published and the file matched it.</summary>
    Verified,

    /// <summary>The file downloaded, but nothing proves it is the file the author published.</summary>
    Unverified,

    /// <summary>The file is not what the manifest describes. Never installed.</summary>
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
    /// <summary>A failed report is never installable; the dialog informs, it does not ask.</summary>
    public bool Blocks => Verdict == DownloadVerdict.Failed;

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
