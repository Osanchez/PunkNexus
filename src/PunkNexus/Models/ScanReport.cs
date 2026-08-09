using System.Text.Json.Serialization;

namespace PunkNexus.Models;

// Virus scan reports, produced by tools/virus-scan.py and published in the PunkNexus repo's
// reports/ folder. One index file carries the newest scan for every catalog artifact; the per-mod
// files next to it keep the history and are for humans reading the repo, not for the client.
//
// A report is identified by the sha256 the SCANNER computed from the bytes it downloaded — never
// by the sha256 a developer declared in their mod.json. That is the whole point of the thing: the
// declared hash proves a download arrived intact, but its author controls it, so it cannot say
// anything about whether the contents were ever inspected. Matching the scanner's hash against
// the file in hand is what makes "this report describes THIS download" a true statement.

public sealed class ScanIndex
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("generatedUtc")] public string? GeneratedUtc { get; set; }
    [JsonPropertyName("scans")] public List<ScanRecord> Scans { get; set; } = new();

    public ScanRecord? Find(string modId) =>
        Scans.FirstOrDefault(s => string.Equals(s.ModId, modId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The report for one exact file, or null when nothing has scanned those bytes.</summary>
    public ScanRecord? FindByHash(string sha256) =>
        string.IsNullOrWhiteSpace(sha256)
            ? null
            : Scans.FirstOrDefault(s => string.Equals(s.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
}

public sealed class ScanRecord
{
    [JsonPropertyName("modId")] public string ModId { get; set; } = "";
    [JsonPropertyName("modName")] public string? ModName { get; set; }

    /// <summary>The scanner's own hash of the scanned file. The report's identity.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";

    [JsonPropertyName("fileName")] public string? FileName { get; set; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }

    /// <summary>Mod version the scanned file belonged to, so a newer release can be spotted.</summary>
    [JsonPropertyName("modVersion")] public string? ModVersion { get; set; }

    /// <summary>Whether the author's declared checksum agreed with the file that was scanned.</summary>
    [JsonPropertyName("declaredMatches")] public bool? DeclaredMatches { get; set; }

    [JsonPropertyName("scannedUtc")] public string? ScannedUtc { get; set; }
    [JsonPropertyName("analyzedUtc")] public string? AnalyzedUtc { get; set; }

    /// <summary><c>completed</c>, <c>queued</c> or <c>error</c>.</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }

    /// <summary>Engines that returned a verdict — not every engine VirusTotal lists.</summary>
    [JsonPropertyName("enginesTotal")] public int EnginesTotal { get; set; }

    [JsonPropertyName("detections")] public int Detections { get; set; }

    /// <summary>"Engine: verdict" for each engine that flagged the file.</summary>
    [JsonPropertyName("detectedBy")] public List<string> DetectedBy { get; set; } = new();

    [JsonPropertyName("permalink")] public string? Permalink { get; set; }
    [JsonPropertyName("reportPath")] public string? ReportPath { get; set; }

    public bool IsComplete =>
        string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase) && EnginesTotal > 0;

    public bool HasDetections => Detections > 0;

    /// <summary>Local date of the scan, or an empty string when the report never completed.</summary>
    public string ScannedOn
    {
        get
        {
            var stamp = AnalyzedUtc ?? ScannedUtc;
            return DateTimeOffset.TryParse(stamp, out var when)
                ? when.ToLocalTime().ToString("d MMMM yyyy")
                : "";
        }
    }

    /// <summary>
    /// What was checked, stated as a fact rather than as a verdict. Deliberately never renders
    /// "clean" or "safe": a scan is evidence about one file at one moment, and a launcher that
    /// promotes it to a safety judgement is making a promise it cannot keep.
    /// </summary>
    public string Coverage =>
        !IsComplete ? "Not scanned yet"
        : $"Scanned {ScannedOn} by {EnginesTotal} engines";

    public string DetectionText =>
        !IsComplete ? ""
        : Detections == 0 ? $"No engine flagged this file ({EnginesTotal} checked)"
        : $"{Detections} of {EnginesTotal} engines flagged it";
}
