using System.Diagnostics;
using System.Security.Cryptography;
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
/// Everything it needs about a mod comes from that mod's own manifest, so the client never has to
/// be told twice what a mod is. Every written file is recorded, so an uninstall removes exactly
/// what was added.
/// </summary>
public sealed class InstallService
{
    private const string LoaderMarkerFile = "winhttp.dll";
    private const string PluginsRelative = "BepInEx/plugins";

    private readonly HttpClient _http;
    private readonly ReleaseResolver _resolver;
    private readonly InstallStateStore _store;

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

    public async Task InstallLoaderAsync(
        string gameRoot, LoaderEntry loader, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        GuardGameFolder(gameRoot);

        var source = loader.Source ?? new AssetSource { Url = loader.DownloadUrl };
        var asset = await _resolver.ResolveAsync(source, ct).ConfigureAwait(false)
                    ?? throw new InstallException(
                        "Could not work out where to download BepInEx from. Check your connection " +
                        "and try again.");

        var zip = await DownloadAsync(asset, loader.Sha256, progress, ct).ConfigureAwait(false);
        try
        {
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
        }
        finally
        {
            TryDelete(zip);
        }
    }

    public async Task InstallModAsync(
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

        var zip = await DownloadAsync(asset, manifest.Sha256, progress, ct).ConfigureAwait(false);
        try
        {
            progress?.Report(new InstallProgress($"Installing {manifest.Name}"));

            // Replacing an existing copy: drop the old files first so a renamed DLL cannot linger
            // and get loaded alongside the new one.
            if (IsModInstalled(gameRoot, manifest.EffectivePluginFolder))
                RemoveModFiles(gameRoot, manifest.Id, manifest.EffectivePluginFolder, manifest.Name, keepState: true);

            var written = ZipSafe.Extract(zip, gameRoot, null, ct).ToList();

            // The zip is required to carry mod.json, but write it if the author forgot: the
            // installed manifest is how every later run knows what version is on disk, and an
            // install that silently lacks one would read as "unknown version" forever.
            EnsureInstalledManifest(gameRoot, manifest, written);

            var state = _store.Load(gameRoot);
            state.Mods[manifest.Id] = new InstalledArtifact
            {
                Id = manifest.Id,
                Version = manifest.Version,
                InstalledUtc = DateTime.UtcNow.ToString("o"),
                Files = written,
            };
            _store.Save(gameRoot, state);

            Log.Info($"Installed {manifest.Id} {manifest.Version} ({written.Count} files).");
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
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
                manifest, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

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
        ResolvedAsset asset, string? expectedSha256, IProgress<InstallProgress>? progress, CancellationToken ct)
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

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            progress?.Report(new InstallProgress("Verifying download"));
            var actual = await Sha256Async(target, ct).ConfigureAwait(false);
            if (!actual.Equals(expectedSha256!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(target);
                throw new InstallException(
                    $"{asset.FileName} did not match its expected checksum, so nothing was installed. " +
                    "Try again; if it keeps failing, report it.");
            }
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
