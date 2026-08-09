using PunkNexus.Models;

namespace PunkNexus.Services;

/// <summary>
/// Turns a published scan report into the modal a user reads.
///
/// The framing here is deliberate, and it is the reason this lives in one place instead of being
/// assembled inline wherever a report is shown.
///
/// BepInEx mods are unsigned .NET assemblies whose entire purpose is to patch a running process.
/// That is, in the abstract, indistinguishable from what heuristic antivirus engines are built to
/// catch, so a handful of detections across ~70 engines is the ordinary result for honest work in
/// this community. Printing "4/70 detected" as if it were a verdict would libel the mod authors
/// this client exists to distribute — so the number is never the headline, it never appears
/// without the context that explains it, and it never gates anything.
///
/// What IS meaningful, and what the modal leads with: was this exact file scanned, when, and by
/// how many engines. Everything else is supporting detail, and the VirusTotal permalink is there
/// so nobody has to accept this window's reading of it.
/// </summary>
public static class ScanDialog
{
    /// <summary>Individual engine verdicts shown before the list is cut off.</summary>
    private const int MaxEnginesShown = 8;

    private const string HeuristicNote =
        "Mods are unsigned code that patches the running game, which is exactly what behavior-based " +
        "antivirus engines look for. A few detections on a legitimate mod are common and are not on " +
        "their own evidence of anything.";

    public static DialogRequest Build(
        string modName, ScanRecord? scan, string? publishedVersion, bool reportsAvailable)
    {
        var details = new List<DialogDetail>();

        // "We could not read the reports" and "there is no report" are different facts, and only
        // one of them is about the mod. Saying the first as if it were the second would put an
        // unearned mark against a mod because the user's connection was down.
        if (!reportsAvailable)
        {
            return new DialogRequest
            {
                Title = $"Security scan — {modName}",
                Kind = DialogKind.Info,
                Message =
                    "The published scan reports could not be loaded, so there is nothing to show " +
                    "for this mod right now. This is a problem reaching the catalog, not a finding " +
                    "about the mod.",
                Details = new[] { new DialogDetail("Refresh the list to try again.") },
                AcceptText = "Close",
            };
        }

        if (scan is null || !scan.IsComplete)
        {
            details.Add(new DialogDetail(
                "Scans run automatically and only when a mod's file changes, so a new or " +
                "just-updated mod is normally unscanned for a while."));
            details.Add(new DialogDetail(
                "Nothing about this mod has been checked either way — this is an absence of " +
                "information, not a warning."));

            return new DialogRequest
            {
                Title = $"Security scan — {modName}",
                Kind = DialogKind.Info,
                Message = scan is null
                    ? $"No virus scan has been published for {modName} yet."
                    : $"A scan of {modName} has been submitted, but no results have come back yet.",
                Details = details,
                AcceptText = "Close",
            };
        }

        // Lead with which file and when. That is the part that is actually a fact; everything
        // after it is qualification.
        var named = string.IsNullOrWhiteSpace(scan.FileName) ? "The download" : scan.FileName!;
        details.Add(new DialogDetail(
            $"{named} · {DownloadReport.FormatSize(scan.SizeBytes)} · SHA-256 {Short(scan.Sha256)}"));

        details.Add(new DialogDetail(scan.Coverage, true));

        if (scan.DeclaredMatches == true)
            details.Add(new DialogDetail("The author's published checksum matches the scanned file", true));

        // A mod can be released between scheduled scans, and the claim "this is the file you would
        // download" is only true when it has not. Never assert it on the strength of the mod id
        // alone — the hash is what ties a report to a file, and the client re-checks it against the
        // real bytes at install time.
        if (!string.IsNullOrWhiteSpace(publishedVersion) &&
            !string.IsNullOrWhiteSpace(scan.ModVersion) &&
            !string.Equals(publishedVersion, scan.ModVersion, StringComparison.OrdinalIgnoreCase))
        {
            details.Add(new DialogDetail(
                $"This report covers v{scan.ModVersion}. The current release is v{publishedVersion}, " +
                "which has not been scanned yet."));
        }
        else if (!string.IsNullOrWhiteSpace(scan.ModVersion))
        {
            details.Add(new DialogDetail(
                $"v{scan.ModVersion} is the current release, so this covers the file you would " +
                "install now", true));
        }

        if (scan.HasDetections)
        {
            // Neutral markers throughout. A red cross next to an engine name would turn a
            // heuristic guess into an accusation.
            details.Add(new DialogDetail(scan.DetectionText));

            foreach (var hit in scan.DetectedBy.Take(MaxEnginesShown))
                details.Add(new DialogDetail($"    {hit}"));

            if (scan.DetectedBy.Count > MaxEnginesShown)
                details.Add(new DialogDetail(
                    $"    and {scan.DetectedBy.Count - MaxEnginesShown} more — see the full report"));

            details.Add(new DialogDetail(HeuristicNote));
        }
        else
        {
            details.Add(new DialogDetail(scan.DetectionText, true));
        }

        details.Add(new DialogDetail(
            "Scan results are shown for information. PUNK Nexus never blocks an install on them."));

        var message = scan.HasDetections
            ? $"{modName} was checked against {scan.EnginesTotal} antivirus engines on {scan.ScannedOn}. " +
              $"{scan.Detections} flagged it."
            : $"{modName} was checked against {scan.EnginesTotal} antivirus engines on {scan.ScannedOn}. " +
              "None flagged it.";

        return new DialogRequest
        {
            Title = $"Security scan — {modName}",
            // Info even when engines flagged the file. Severity coloring here would be the app
            // making the judgement it just said it was not making.
            Kind = DialogKind.Info,
            Message = message,
            Details = details,
            AcceptText = "Close",
            LinkText = "Open the full report",
            LinkUrl = scan.Permalink,
        };
    }

    private static string Short(string hash) =>
        hash.Length <= 16 ? hash : $"{hash[..8]}…{hash[^8..]}";
}
