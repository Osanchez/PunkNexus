using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PunkNexus.Models;

namespace PunkNexus.Services;

public sealed record InstallProgress(string Stage, double? Fraction = null);

/// <summary>Raised for conditions the user can act on; the message is shown verbatim.</summary>
public sealed class InstallException : Exception
{
    public InstallException(string message) : base(message) { }
    public InstallException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Downloads and applies BepInEx and mods to a verified game folder.
///
/// Nothing is written to the game folder until the download has been inspected and the user has
/// seen what was checked. Everything written is recorded, so an uninstall removes exactly what was
/// added.
/// </summary>
public sealed class InstallService
{
    private const string LoaderMarkerFile = "winhttp.dll";
    private const string PluginsRelative = "BepInEx/plugins";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly HttpClient _http;
    private readonly ReleaseResolver _resolver;
    private readonly InstallStateStore _store;

    /// <summary>
    /// Shown between download and extraction. Returns false to abandon the install. Left unset the
    /// service still refuses a failed verification — the prompt reports, it does not authorize.
    /// </summary>
    public Func<DownloadReport, Task<bool>>? ConfirmDownload { get; set; }

    /// <summary>
    /// The published virus scan index, when one has been loaded. Supplied as a hook rather than a
    /// constructor dependency for the same reason as <see cref="ConfirmDownload"/>: the installer
    /// works without it, and an unreachable reports file must never be able to stop an install.
    ///
    /// Returning null means "we do not know", which is reported differently from "no scan exists" —
    /// an offline client claiming a mod is unscanned would be inventing information.
    /// </summary>
    public Func<ScanIndex?>? PublishedScans { get; set; }

    public InstallService(HttpClient http, ReleaseResolver resolver, InstallStateStore store)
    {
        _http = http;
        _resolver = resolver;
        _store = store;
    }

    // ---------------------------------------------------------------- queries

    public bool IsLoaderInstalled(string gameRoot) =>
        File.Exists(Path.Combine(gameRoot, LoaderMarkerFile)) &&
        Directory.Exists(Path.Combine(gameRoot, "BepInEx", "core"));

    public static bool IsModInstalled(string gameRoot, string pluginFolder) =>
        Directory.Exists(PluginFolderPath(gameRoot, pluginFolder));

    // ---------------------------------------------------------------- install

    /// <returns>False when the user declined at the verification prompt.</returns>
    public async Task<bool> InstallLoaderAsync(
        string gameRoot, LoaderEntry loader, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        GuardGameFolder(gameRoot);

        var source = loader.Source ?? new AssetSource { Url = loader.DownloadUrl };
        var asset = await _resolver.ResolveAsync(source, ct).ConfigureAwait(false)
                    ?? throw new InstallException(
                        "Could not work out where to download BepInEx from. Check your connection " +
                        "and try again.");

        var zip = await DownloadAsync(asset, progress, ct).ConfigureAwait(false);
        try
        {
            // No scan id: BepInEx is not scanned. It is the loader itself rather than
            // community-submitted mod content, so the catalog files no report for it and the
            // dialog says nothing about scanning rather than reporting a gap that is deliberate.
            var report = await VerifyAsync(zip, asset, loader.Sha256, null, loader.Name, null, progress, ct)
                .ConfigureAwait(false);

            if (!await ConfirmAsync(report).ConfigureAwait(false)) return false;

            progress?.Report(new InstallProgress("Installing BepInEx"));
            var written = ZipSafe.Extract(zip, gameRoot, null, ct);

            var state = _store.Load(gameRoot);
            state.Loader = new InstalledArtifact
            {
                Id = "BepInEx",
                Version = asset.Version ?? loader.Version,
                InstalledUtc = DateTime.UtcNow.ToString("o"),
                Files = written.ToList(),
            };
            _store.Save(gameRoot, state);

            Log.Info($"Installed BepInEx ({written.Count} files) into {gameRoot}.");
            return true;
        }
        finally
        {
            TryDelete(zip);
        }
    }

    /// <returns>False when the user declined at the verification prompt.</returns>
    public async Task<bool> InstallModAsync(
        string gameRoot, ModManifest manifest, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        GuardGameFolder(gameRoot);

        if (!IsLoaderInstalled(gameRoot))
            throw new InstallException(
                "BepInEx is not installed yet. Install it first — mods cannot load without it.");

        var asset = await _resolver.ResolveAsync(manifest.Download, ct).ConfigureAwait(false)
                    ?? throw new InstallException(
                        $"No download is published for {manifest.Name} yet. Its manifest does not " +
                        "point at a downloadable release.");

        var zip = await DownloadAsync(asset, progress, ct).ConfigureAwait(false);
        try
        {
            var report = await VerifyAsync(zip, asset, manifest.Sha256, manifest.Id, manifest.Name, manifest, progress, ct)
                .ConfigureAwait(false);

            if (!await ConfirmAsync(report).ConfigureAwait(false)) return false;

            progress?.Report(new InstallProgress($"Installing {manifest.Name}"));

            // Replacing an existing copy: drop the old files first so a renamed DLL cannot linger
            // and get loaded alongside the new one.
            if (IsModInstalled(gameRoot, manifest.EffectivePluginFolder))
                RemoveModFiles(gameRoot, manifest.Id, manifest.EffectivePluginFolder, manifest.Name, keepState: true);

            var written = ZipSafe.Extract(zip, gameRoot, null, ct).ToList();
            EnsureInstalledManifest(gameRoot, manifest, written);

            var state = _store.Load(gameRoot);
            state.Mods[manifest.Id] = new InstalledArtifact
            {
                Id = manifest.Id,
                Version = manifest.Version,
                InstalledUtc = DateTime.UtcNow.ToString("o"),
                PluginFolder = manifest.EffectivePluginFolder,
                Files = written,
            };
            _store.Save(gameRoot, state);

            Log.Info($"Installed {manifest.Id} {manifest.Version} ({written.Count} files).");
            return true;
        }
        finally
        {
            TryDelete(zip);
        }
    }

    public Task UninstallModAsync(string gameRoot, string id, string pluginFolder, string displayName, CancellationToken ct)
    {
        GuardGameFolder(gameRoot);

        RemoveModFiles(gameRoot, id, pluginFolder, displayName, keepState: false);
        Log.Info($"Removed {id} from {gameRoot}.");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- verification

    /// <summary>
    /// Everything checkable between "bytes arrived" and "files written": the publisher's checksum,
    /// the manifest packaged inside the archive, and whether a published virus scan covers these
    /// exact bytes. Runs before extraction so a bad archive costs the user nothing.
    /// </summary>
    private async Task<DownloadReport> VerifyAsync(
        string zipPath,
        ResolvedAsset asset,
        string? expectedSha256,
        string? modId,
        string modName,
        ModManifest? published,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var details = new List<DialogDetail>();
        var size = new FileInfo(zipPath).Length;
        var failed = false;
        var unverified = false;
        var checksumMismatch = false;

        // Hashed unconditionally, not only when a checksum was published: the scan report is filed
        // under the scanner's own hash of the file, so without this there is no way to say whether
        // a report describes the download in hand. Costs microseconds on a 20 KB zip.
        progress?.Report(new InstallProgress("Verifying checksum"));
        var actual = await Sha256Async(zipPath, ct).ConfigureAwait(false);

        // ---- publisher checksum
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            if (actual.Equals(expectedSha256!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                details.Add(new DialogDetail($"SHA-256 matches the published checksum ({Short(actual)})", true));
            }
            else
            {
                failed = true;
                checksumMismatch = true;
                details.Add(new DialogDetail($"SHA-256 does not match. Expected {Short(expectedSha256!)}, got {Short(actual)}", false));
            }
        }
        else
        {
            unverified = true;
            details.Add(new DialogDetail("No checksum was published, so the file could not be verified", null));
        }

        // ---- the manifest inside the archive
        if (published is not null)
        {
            var entry = $"{PluginsRelative}/{published.EffectivePluginFolder}/{ModManifest.FileName}";
            var json = ZipSafe.TryReadTextEntry(zipPath, entry);

            if (json is null)
            {
                unverified = true;
                details.Add(new DialogDetail($"The archive contains no {ModManifest.FileName}", null));
            }
            else
            {
                ModManifest? packaged = null;
                try { packaged = JsonSerializer.Deserialize<ModManifest>(json, ManifestJson); }
                catch (Exception ex) { Log.Warn($"Packaged manifest is unreadable: {ex.Message}"); }

                if (packaged is null)
                {
                    unverified = true;
                    details.Add(new DialogDetail($"The packaged {ModManifest.FileName} could not be read", null));
                }
                else if (!string.IsNullOrWhiteSpace(packaged.Id) &&
                         !string.Equals(packaged.Id, published.Id, StringComparison.OrdinalIgnoreCase))
                {
                    // A different mod than the one listed. Whatever this is, it is not what the
                    // user asked for.
                    failed = true;
                    details.Add(new DialogDetail(
                        $"The archive contains '{packaged.Id}', not '{published.Id}'", false));
                }
                else
                {
                    details.Add(new DialogDetail($"Archive contents identify as {published.Id}", true));

                    if (!string.IsNullOrWhiteSpace(packaged.Version) &&
                        !string.Equals(packaged.Version, published.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        unverified = true;
                        details.Add(new DialogDetail(
                            $"Packaged version is {packaged.Version}, but the listing says {published.Version}", null));
                    }
                    else if (!string.IsNullOrWhiteSpace(packaged.Version))
                    {
                        details.Add(new DialogDetail($"Version {packaged.Version} matches the listing", true));
                    }
                }
            }
        }

        // ---- the published virus scan
        //
        // Evidence, never a gate. The checksum above blocks because a mismatch is a definite
        // integrity failure with no innocent reading; a scan result has no such property — mods
        // are unsigned code that patches a running game, which heuristic engines flag routinely.
        // So nothing below touches `failed` or `unverified`. It reports and gets out of the way.
        details.AddRange(ScanDetails(modId, actual));

        var verdict = failed ? DownloadVerdict.Failed
            : unverified ? DownloadVerdict.Unverified
            : DownloadVerdict.Verified;

        var (headline, message) = verdict switch
        {
            DownloadVerdict.Verified => (
                "Download verified",
                $"{modName} downloaded and matches the checksum its author published. Install it?"),
            DownloadVerdict.Unverified => (
                "Download complete — not verified",
                $"{modName} downloaded, but nothing proves it is exactly what its author published. " +
                "Only continue if you trust the source."),
            _ => (
                "Download rejected",
                $"{modName} is not what its manifest describes, so nothing was installed. " +
                "This can mean a corrupted download or a file that has been altered."),
        };

        return new DownloadReport(modName, asset.FileName, size, verdict, headline, message, details)
        {
            ChecksumMismatch = checksumMismatch,
        };
    }

    /// <summary>
    /// Whether a published scan covers the exact bytes just downloaded, in one or two lines.
    ///
    /// Three genuinely different answers, and conflating any two of them would mislead: the report
    /// covers this file, the report covers a different build of this mod, or no report exists. A
    /// fourth case — the reports file could not be loaded at all — says nothing rather than
    /// claiming the mod is unscanned.
    /// </summary>
    private IEnumerable<DialogDetail> ScanDetails(string? modId, string actualSha256)
    {
        // A null id means this download is deliberately outside the scanned set, which is not the
        // same as an unscanned mod and must not be reported as one.
        if (string.IsNullOrWhiteSpace(modId)) yield break;

        var index = PublishedScans?.Invoke();
        if (index is null) yield break;

        var scan = index.Find(modId!);

        if (scan is null || !scan.IsComplete)
        {
            yield return new DialogDetail(
                "No virus scan has been published for this download yet — that is normal for a " +
                "new or just-updated mod, and says nothing about it either way");
            yield break;
        }

        if (!string.Equals(scan.Sha256, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            var which = string.IsNullOrWhiteSpace(scan.ModVersion) ? "an earlier build" : $"v{scan.ModVersion}";
            yield return new DialogDetail(
                $"The published scan covers {which}, not this file. This exact download has not " +
                "been scanned yet");
            yield break;
        }

        if (scan.Detections == 0)
        {
            yield return new DialogDetail(
                $"Virus scan: this exact file was scanned {scan.ScannedOn} — none of " +
                $"{scan.EnginesTotal} engines flagged it", true);
            yield break;
        }

        // Neutral marker and the explanation on the same screen. A count on its own, next to a red
        // cross, is how honest mods get mistaken for malware.
        yield return new DialogDetail(
            $"Virus scan: this exact file was scanned {scan.ScannedOn} — {scan.Detections} of " +
            $"{scan.EnginesTotal} engines flagged it. Unsigned mods commonly trip heuristic " +
            "detection; this does not block the install");
    }

    /// <summary>
    /// Shows the report and returns whether to proceed. A failed verification never installs,
    /// whatever the answer — and with no prompt wired up it still refuses.
    /// </summary>
    /// <summary>
    /// Shows the report and returns whether to proceed.
    ///
    /// A checksum mismatch is refused here, after the dialog rather than instead of it: the user
    /// still gets told exactly what was found, they just are not offered a way past it. Doing the
    /// refusal in the service and not in the UI is deliberate — the guarantee then holds for every
    /// caller, including one that forgets to set <see cref="ConfirmDownload"/> at all.
    /// </summary>
    private async Task<bool> ConfirmAsync(DownloadReport report)
    {
        var accepted = ConfirmDownload is null || await ConfirmDownload(report).ConfigureAwait(true);

        if (report.ChecksumMismatch)
        {
            // Always at error level, whether or not anyone was watching. A refused install is
            // exactly the event someone reads the log to find afterwards.
            Log.Error($"CHECKSUM MISMATCH for {report.ModName}: "
                      + string.Join("; ", report.Details.Where(d => d.Ok == false).Select(d => d.Text))
                      + " — install refused.");
            return false;
        }

        if (!accepted) Log.Info($"User declined to install {report.ModName} after verification.");
        return accepted;
    }

    private static string Short(string hash) =>
        hash.Length <= 16 ? hash : $"{hash[..8]}…{hash[^8..]}";

    /// <summary>
    /// Guarantees the plugin folder holds the manifest that describes what was just installed.
    /// A well-packaged zip already contains it and this is a no-op.
    /// </summary>
    private static void EnsureInstalledManifest(string gameRoot, ModManifest manifest, List<string> written)
    {
        var folder = PluginFolderPath(gameRoot, manifest.EffectivePluginFolder);
        var path = Path.Combine(folder, ModManifest.FileName);
        if (File.Exists(path)) return;

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, JsonSerializer.Serialize(
                manifest, new JsonSerializerOptions { WriteIndented = true }));

            var relative = $"{PluginsRelative}/{manifest.EffectivePluginFolder}/{ModManifest.FileName}";
            if (!written.Contains(relative)) written.Add(relative);

            Log.Warn($"{manifest.Id} shipped no {ModManifest.FileName}; wrote one from the published manifest.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write the installed manifest for {manifest.Id}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- removal

    private void RemoveModFiles(string gameRoot, string id, string pluginFolder, string displayName, bool keepState)
    {
        var root = Path.GetFullPath(gameRoot);
        var state = _store.Load(gameRoot);

        if (state.Mods.TryGetValue(id, out var recorded))
        {
            foreach (var relative in recorded.Files)
            {
                var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsInside(root, full)) continue;   // never delete outside the game folder
                TryDelete(full);
            }
        }

        // Also remove the plugin folder itself: it accumulates files we did not write (config.cfg
        // and runtime assets), and leaving those behind means a "removed" mod keeps its settings.
        var folder = PluginFolderPath(gameRoot, pluginFolder);
        if (IsInside(root, folder) && Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                throw new InstallException(
                    $"Could not remove {displayName}: {ex.Message} " +
                    "Close the game if it is running and try again.", ex);
            }
        }

        if (!keepState)
        {
            state.Mods.Remove(id);
            _store.Save(gameRoot, state);
        }
    }

    // ---------------------------------------------------------------- download

    private async Task<string> DownloadAsync(
        ResolvedAsset asset, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        AppPaths.EnsureCreated();
        var target = Path.Combine(AppPaths.DownloadsDir, $"{Guid.NewGuid():N}-{asset.FileName}");

        try
        {
            using var response = await _http
                .GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? (asset.SizeBytes > 0 ? asset.SizeBytes : 0);
            var label = $"Downloading {asset.FileName}";

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var destination = File.Create(target))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied += read;
                    progress?.Report(new InstallProgress(label, total > 0 ? copied / (double)total : null));
                }
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(target);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(target);
            throw new InstallException($"Download failed for {asset.FileName}: {ex.Message}", ex);
        }

        return target;
    }

    private static async Task<string> Sha256Async(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    // ---------------------------------------------------------------- helpers

    private static string PluginFolderPath(string gameRoot, string pluginFolder) =>
        Path.GetFullPath(Path.Combine(gameRoot,
            PluginsRelative.Replace('/', Path.DirectorySeparatorChar),
            pluginFolder));

    private static bool IsInside(string root, string candidate)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-checks the folder immediately before writing. The install may have been moved, deleted or
    /// made read-only since setup, and the game holds its DLLs open while it runs.
    /// </summary>
    private static void GuardGameFolder(string gameRoot)
    {
        var verification = GameLocator.Verify(gameRoot);
        if (!verification.IsValid)
            throw new InstallException(
                verification.Problem ?? "The game folder is no longer valid. Re-run setup.");

        if (IsGameRunning())
            throw new InstallException(
                "PUNK is running. Close the game before installing or removing mods — its files are " +
                "locked while it is open.");
    }

    private static bool IsGameRunning()
    {
        try
        {
            return Process.GetProcessesByName("Punk").Length > 0;
        }
        catch
        {
            // Enumerating processes can fail under restricted tokens; the install will surface a
            // file-lock error instead.
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete {path}: {ex.Message}");
        }
    }
}
