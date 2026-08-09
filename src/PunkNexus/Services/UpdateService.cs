using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PunkNexus.Services;

/// <summary>What a check found, if anything.</summary>
public sealed record AvailableUpdate(Version Version, string DownloadUrl, string? Sha256, string? Notes);

/// <summary>
/// Keeps the client current.
///
/// This client is the thing that decides what a player installs and verifies what those downloads
/// are, so an old copy is not merely missing features -- it is missing whatever the newer one knows
/// about refusing bad files. Every fix from this week's testing (an archive that could overwrite
/// another mod, a mod list that named the same mod twice, scan reports that silently loaded none)
/// only protects someone actually running that build. Hence: found at startup, offered once, and
/// declining closes the app rather than continuing on a version we have reason to replace.
///
/// The updater is also the most dangerous code here, because it fetches an executable and runs it.
/// It therefore holds itself to the rule the rest of the client applies to mods: a published hash
/// is a promise, and a download that does not match it is refused. Absent a published hash the
/// update is refused outright -- for a mod that is the author's choice to make, but this binary
/// replaces the client itself and there is no one else to carry the risk.
/// </summary>
public sealed class UpdateService
{
    private const string LatestRelease = "https://api.github.com/repos/Osanchez/PunkNexus/releases/latest";
    private const string ExeAsset = "PunkNexus.exe";
    private const string HashAsset = "PunkNexus.exe.sha256";

    private readonly HttpClient _http;

    public UpdateService(HttpClient http) => _http = http;

    /// <summary>The running build. Releases are tagged with this exact version.</summary>
    public static Version Current { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>
    /// Ask GitHub what the newest release is. Returns null when we are current, and also when the
    /// check simply fails -- being offline must never stop the app from opening, so a failed check
    /// is logged and treated as "nothing to do".
    /// </summary>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct)
    {
        // A build that was not produced by CI carries the csproj placeholder (0.x). Those are
        // developer builds run straight from `dotnet publish`, and they would otherwise see every
        // real release as newer, nag on every launch, and close when declined. Gating releases is
        // the point; gating the person building it is just obstruction.
        if (Current.Major == 0)
        {
            Log.Info($"Development build ({Current}); skipping the update check.");
            return null;
        }

        try
        {
            using var response = await _http.GetAsync(LatestRelease, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Update check returned {(int)response.StatusCode}; carrying on.");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);
            if (release?.TagName is null) return null;
            if (release.Draft || release.Prerelease) return null;

            // Tags are "v2026.08.09.17"; the assembly carries the same numbers. Parsed rather than
            // string-compared, because "2026.08.09" and "2026.8.9" are the same release.
            if (!Version.TryParse(release.TagName.TrimStart('v', 'V'), out var latest))
            {
                Log.Warn($"Could not read a version out of release tag '{release.TagName}'.");
                return null;
            }
            if (latest <= Current) return null;

            var exe = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, ExeAsset, StringComparison.OrdinalIgnoreCase));
            if (exe?.DownloadUrl is null)
            {
                Log.Warn($"Release {release.TagName} publishes no {ExeAsset}; ignoring it.");
                return null;
            }

            var hashAsset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, HashAsset, StringComparison.OrdinalIgnoreCase));
            string? sha = null;
            if (hashAsset?.DownloadUrl is not null)
            {
                var text = await _http.GetStringAsync(hashAsset.DownloadUrl, ct).ConfigureAwait(false);
                sha = text.Split(' ', '\n', '\r').FirstOrDefault(t => t.Length == 64)?.ToLowerInvariant();
            }

            Log.Info($"Update available: {Current} -> {latest}.");
            return new AvailableUpdate(latest, exe.DownloadUrl, sha, release.Body);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Download the new build, verify it, and hand over to it.
    ///
    /// A running executable cannot overwrite itself on Windows, so the swap is done by a detached
    /// script that waits for this process to exit first. It only ever touches two paths, both
    /// beside the current exe, and if the move fails it puts the old file back rather than leaving
    /// the user with nothing to launch.
    /// </summary>
    public async Task ApplyAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(update.Sha256))
            throw new InstallException(
                "That release publishes no checksum, so the download cannot be verified. "
                + "Update was not applied.");

        var current = Environment.ProcessPath
                      ?? throw new InstallException("Could not work out where this program is running from.");
        var folder = Path.GetDirectoryName(current)!;
        var staged = Path.Combine(folder, "PunkNexus.update.exe");

        Log.Info($"Downloading {update.Version} to {staged}.");
        using (var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(staged);

            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(done / (double)total);
            }
        }

        string actual;
        await using (var stream = File.OpenRead(staged))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();

        if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(staged);
            throw new InstallException(
                $"The downloaded update does not match its published checksum "
                + $"(expected {update.Sha256[..12]}…, got {actual[..12]}…). It was discarded.");
        }

        Log.Info("Update verified; handing over.");
        StartSwap(current, staged);
    }

    /// <summary>
    /// Spawn the swap and leave. PowerShell rather than a batch file: quoting a path with spaces is
    /// survivable here, and "Program Files (x86)" has both spaces and parentheses.
    /// </summary>
    private static void StartSwap(string current, string staged)
    {
        var pid = Environment.ProcessId;
        var script =
            $"$ErrorActionPreference='SilentlyContinue';" +
            $"try {{ Wait-Process -Id {pid} -Timeout 30 }} catch {{}};" +
            $"$cur='{current}'; $new='{staged}'; $bak=\"$cur.old\";" +
            // Move the old file aside rather than deleting it: if the rename of the new one fails,
            // there is still something to put back.
            "Remove-Item $bak -Force -ErrorAction SilentlyContinue;" +
            "Move-Item $cur $bak -Force;" +
            "Move-Item $new $cur -Force;" +
            "if (-not (Test-Path $cur)) { Move-Item $bak $cur -Force }" +
            "else { Remove-Item $bak -Force -ErrorAction SilentlyContinue };" +
            "Start-Process -FilePath $cur";

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            ArgumentList = { "-NoProfile", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---- the slice of GitHub's release JSON we actually read ------------------------------------

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; set; }
    }
}
