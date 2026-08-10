using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PunkNexus.Services;

/// <summary>
/// What a check found, if anything.
///
/// <paramref name="IsArchive"/> says which asset <paramref name="DownloadUrl"/> points at. The
/// release publishes the same binary twice — as a bare exe and zipped — and the zip is well under
/// half the size, because a self-contained .NET single file is almost entirely compressible.
/// <paramref name="InnerExeSha256"/> is the hash of the exe inside it, when the release publishes
/// one, so the unpacked file is checked as well as the archive it came out of.
/// </summary>
public sealed record AvailableUpdate(
    Version Version,
    string DownloadUrl,
    string? Sha256,
    string? Notes,
    bool IsArchive = false,
    string? InnerExeSha256 = null);

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
    private const string ExeHashAsset = "PunkNexus.exe.sha256";
    private const string ZipAsset = "PunkNexus-win-x64.zip";
    private const string ZipHashAsset = "PunkNexus-win-x64.zip.sha256";

    // Both live beside the running exe, because that is the only folder the swap can rename across
    // without crossing a volume. Named so a leftover is recognisable at a glance.
    private const string StagedExe = "PunkNexus.update.exe";
    private const string StagedZip = "PunkNexus.update.zip";

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

            var exeSha = await ReadPublishedHashAsync(release, ExeHashAsset, ct).ConfigureAwait(false);

            // Prefer the archive. It is the same binary the exe asset carries, and it is less than
            // half the bytes -- which is not merely faster but the difference between finishing and
            // not: the whole transfer runs under one HttpClient timeout with no resume, so on a slow
            // line the download size decides whether the update can ever complete.
            var zip = AssetNamed(release, ZipAsset);
            var zipSha = await ReadPublishedHashAsync(release, ZipHashAsset, ct).ConfigureAwait(false);
            if (zip?.DownloadUrl is not null && zipSha is not null)
            {
                Log.Info($"Update available: {Current} -> {latest} (archive).");
                return new AvailableUpdate(latest, zip.DownloadUrl, zipSha, release.Body,
                    IsArchive: true, InnerExeSha256: exeSha);
            }

            // No zip, or one we have no hash for. Falling back to the bare exe rather than refusing:
            // a release built before the zip hash existed is still a real release, and the exe path
            // is verified by exactly the same rule.
            var exe = AssetNamed(release, ExeAsset);
            if (exe?.DownloadUrl is null)
            {
                Log.Warn($"Release {release.TagName} publishes no {ExeAsset}; ignoring it.");
                return null;
            }

            Log.Info($"Update available: {Current} -> {latest} (bare exe).");
            return new AvailableUpdate(latest, exe.DownloadUrl, exeSha, release.Body);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            return null;
        }
    }

    private static GitHubAsset? AssetNamed(GitHubRelease release, string name) =>
        release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Read one of the release's .sha256 files, or null if it publishes none.
    ///
    /// Hex is required, not just 64 characters. The token this picks out is the sole thing standing
    /// between a downloaded executable and being run, so "something the right length" is not a good
    /// enough test of "a checksum" -- and a malformed file should read as no promise at all rather
    /// than as a promise nothing can satisfy.
    /// </summary>
    private async Task<string?> ReadPublishedHashAsync(GitHubRelease release, string name, CancellationToken ct)
    {
        var asset = AssetNamed(release, name);
        if (asset?.DownloadUrl is null) return null;

        try
        {
            var text = await _http.GetStringAsync(asset.DownloadUrl, ct).ConfigureAwait(false);
            return text.Split(' ', '\n', '\r', '\t')
                       .FirstOrDefault(t => t.Length == 64 && t.All(Uri.IsHexDigit))
                       ?.ToLowerInvariant();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {name} from the release: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Remove any staged download left beside the exe by an earlier run.
    ///
    /// Nothing ever reads one of these without checking it first -- the swap only starts on a path
    /// that has just passed verification -- so a leftover is litter rather than a hazard. It is
    /// still tens of megabytes sitting in the user's folder under a name suggesting the client is
    /// mid-update, left there by any run killed during a download, and nothing else removes it.
    ///
    /// A file another instance is actively downloading is protected by Windows itself: File.Create
    /// holds it exclusively, the delete fails, and TryDelete swallows it.
    /// </summary>
    public static void SweepStagedDownloads()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        var folder = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(folder)) return;

        foreach (var name in new[] { StagedExe, StagedZip })
            if (TryDelete(Path.Combine(folder, name)))
                Log.Info($"Removed a leftover update download: {name}.");
    }

    /// <summary>
    /// Download the new build, verify it, and hand over to it.
    ///
    /// A running executable cannot overwrite itself on Windows, so the swap is done by a detached
    /// script that waits for this process to exit first. It only ever touches two paths, both
    /// beside the current exe, and if the move fails it puts the old file back rather than leaving
    /// the user with nothing to launch.
    ///
    /// Progress is reported in the same shape the mod installer uses, because the caller shows it
    /// the same way. It matters more here than there: this download is the app the user is looking
    /// at, the window is doing nothing else while it runs, and a client that sits silent for the
    /// length of a 40 MB transfer and then vanishes to restart is indistinguishable from one that
    /// has hung.
    /// </summary>
    public async Task ApplyAsync(AvailableUpdate update, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(update.Sha256))
            throw new InstallException(
                "That release publishes no checksum, so the download cannot be verified. "
                + "Update was not applied.");

        var current = Environment.ProcessPath
                      ?? throw new InstallException("Could not work out where this program is running from.");
        var folder = Path.GetDirectoryName(current)!;
        var staged = Path.Combine(folder, StagedExe);

        // For the bare-exe path these are the same file, so the download IS the staged build and
        // the unpack step below is skipped.
        var download = update.IsArchive ? Path.Combine(folder, StagedZip) : staged;

        Log.Info($"Downloading {update.Version} to {download}.");

        // Reported before the request, not after: name resolution and the connection to GitHub can
        // take a couple of seconds on their own, and that is time the overlay would otherwise sit
        // on an empty bar.
        progress?.Report(new InstallProgress("Contacting GitHub"));

        try
        {
            using (var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var target = File.Create(download);

                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;

                    // Sizes as well as a fraction. A bar that has not visibly moved for ten seconds
                    // says nothing about whether bytes are still arriving; a counter does.
                    progress?.Report(total > 0
                        ? new InstallProgress($"Downloading — {Size(done)} of {Size(total)}", done / (double)total)
                        : new InstallProgress($"Downloading — {Size(done)}"));
                }
            }

            // Named rather than folded into the download, because it is the step that decides
            // whether this build gets installed at all, and on a slow disk it is a visible pause.
            progress?.Report(new InstallProgress("Verifying the download against its published checksum"));
            await VerifyAsync(download, update.Sha256!, "download", ct).ConfigureAwait(false);

            if (update.IsArchive)
            {
                progress?.Report(new InstallProgress("Unpacking"));

                // ZipSafe is for archives that land in the game folder at paths the archive itself
                // chooses; that is not this. One entry is pulled out by name to a path picked here,
                // so no entry name is ever used as a destination and there is nothing to escape.
                // The archive also just matched the hash published with the release, so its
                // contents are byte-for-byte what CI built.
                ExtractStagedExe(download, staged);

                // Checked again on the way out, when the release says what the exe should hash to.
                // The archive matching proves the zip is authentic; this proves the file actually
                // written to disk is the build inside it, and costs one pass over a local file.
                if (update.InnerExeSha256 is { } innerHash)
                    await VerifyAsync(staged, innerHash, "unpacked executable", ct).ConfigureAwait(false);

                TryDelete(download);
            }
        }
        catch
        {
            // Anything that goes wrong leaves files that were never confirmed to be the release,
            // sitting next to the exe under the exact name the swap script would move into place.
            // Nothing reads them without checking first, but leaving them there is still leaving a
            // half-downloaded binary in the user's game-adjacent folder for no reason.
            TryDelete(download);
            TryDelete(staged);
            throw;
        }

        Log.Info("Update verified; handing over.");

        // Last thing the window shows. The swap script waits for this process to exit, so the
        // caller shuts down immediately after and the overlay goes with it.
        progress?.Report(new InstallProgress("Installing, then restarting"));
        StartSwap(current, staged);
    }

    /// <summary>Hash a file and refuse it if it is not what the release promised.</summary>
    private static async Task VerifyAsync(string path, string expected, string what, CancellationToken ct)
    {
        string actual;
        await using (var stream = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InstallException(
                $"The {what} does not match its published checksum "
                + $"(expected {expected[..12]}…, got {actual[..12]}…). It was discarded.");
    }

    /// <summary>
    /// Pull the client out of the release archive. The zip holds exactly one file, but it is
    /// matched by name rather than taken as "the first entry": an archive with something else in it
    /// is one this code does not understand, and guessing is how the wrong binary gets installed.
    /// </summary>
    private static void ExtractStagedExe(string zipPath, string target)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var entry = archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, ExeAsset, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InstallException(
                        $"The downloaded archive does not contain {ExeAsset}, so there is nothing to "
                        + "install from it. Update was not applied.");

        entry.ExtractToFile(target, overwrite: true);
    }

    /// <summary>Byte counts as the user reads them. MB throughout: the exe is tens of megabytes,
    /// and a counter that switches units partway through reads as a number going backwards.</summary>
    private static string Size(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";

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
            $"$cur={PsLiteral(current)}; $new={PsLiteral(staged)}; $bak=\"$cur.old\";" +
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

    /// <summary>
    /// A path as a PowerShell single-quoted literal, with embedded quotes doubled the way
    /// PowerShell escapes them.
    ///
    /// The old code interpolated the path straight between quotes, which breaks on any account
    /// whose name contains an apostrophe -- C:\Users\O'Brien\... closes the string early and the
    /// rest of the path is parsed as commands. That is a broken updater for those users, and a
    /// command injection anywhere a path is not entirely the user's own doing. Nothing here is
    /// worth leaving to chance: this script runs after the app has exited, unattended.
    /// </summary>
    private static string PsLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>Delete if present. Returns whether a file was actually removed.</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
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
