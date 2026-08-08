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
/// Every write is recorded so an uninstall removes exactly what was added. Disk is the source of
/// truth for "is it installed" — a mod extracted by hand still shows as installed.
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

    public bool IsModInstalled(string gameRoot, ModEntry mod) =>
        Directory.Exists(PluginFolderPath(gameRoot, mod));

    /// <summary>
    /// The installed version. Prefers our own record, then the mod.yaml the mod ships, so a
    /// hand-extracted zip still reports a version instead of "unknown".
    /// </summary>
    public string? GetInstalledVersion(string gameRoot, ModEntry mod)
    {
        var state = _store.Load(gameRoot);
        if (state.Mods.TryGetValue(mod.Id, out var recorded) && !string.IsNullOrWhiteSpace(recorded.Version))
            return recorded.Version;

        var yaml = Path.Combine(PluginFolderPath(gameRoot, mod), "mod.yaml");
        if (!File.Exists(yaml)) return null;

        try
        {
            foreach (var line in File.ReadLines(yaml))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("version:", StringComparison.OrdinalIgnoreCase)) continue;
                return trimmed[8..].Trim().Trim('"', '\'');
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {yaml}: {ex.Message}");
        }

        return null;
    }

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
        string gameRoot, ModEntry mod, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        GuardGameFolder(gameRoot);

        if (!IsLoaderInstalled(gameRoot))
            throw new InstallException(
                "BepInEx is not installed yet. Install it first — mods cannot load without it.");

        var asset = await _resolver.ResolveAsync(mod.Source, ct).ConfigureAwait(false)
                    ?? throw new InstallException(
                        $"No download is published for {mod.Name} yet. It may not have been released.");

        var zip = await DownloadAsync(asset, mod.Sha256, progress, ct).ConfigureAwait(false);
        try
        {
            progress?.Report(new InstallProgress($"Installing {mod.Name}"));

            // Replacing an existing copy: drop the old files first so a renamed DLL cannot linger
            // and get loaded alongside the new one.
            if (IsModInstalled(gameRoot, mod))
                RemoveModFiles(gameRoot, mod, keepState: true);

            var written = ZipSafe.Extract(zip, gameRoot, null, ct);

            var state = _store.Load(gameRoot);
            state.Mods[mod.Id] = new InstalledArtifact
            {
                Id = mod.Id,
                Version = asset.Version ?? mod.Version,
                InstalledUtc = DateTime.UtcNow.ToString("o"),
                Files = written.ToList(),
            };
            _store.Save(gameRoot, state);

            Log.Info($"Installed {mod.Id} {asset.Version ?? mod.Version} ({written.Count} files).");
        }
        finally
        {
            TryDelete(zip);
        }
    }

    public Task UninstallModAsync(string gameRoot, ModEntry mod, CancellationToken ct)
    {
        GuardGameFolder(gameRoot);

        RemoveModFiles(gameRoot, mod, keepState: false);
        Log.Info($"Removed {mod.Id} from {gameRoot}.");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- removal

    private void RemoveModFiles(string gameRoot, ModEntry mod, bool keepState)
    {
        var root = Path.GetFullPath(gameRoot);
        var state = _store.Load(gameRoot);

        if (state.Mods.TryGetValue(mod.Id, out var recorded))
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
        var pluginFolder = PluginFolderPath(gameRoot, mod);
        if (IsInside(root, pluginFolder) && Directory.Exists(pluginFolder))
        {
            try
            {
                Directory.Delete(pluginFolder, recursive: true);
            }
            catch (Exception ex)
            {
                throw new InstallException(
                    $"Could not remove {mod.Name}: {ex.Message} " +
                    "Close the game if it is running and try again.", ex);
            }
        }

        if (!keepState)
        {
            state.Mods.Remove(mod.Id);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(target);
            throw new InstallException(
                $"Download failed for {asset.FileName}: {ex.Message}", ex);
        }
        catch (OperationCanceledException)
        {
            TryDelete(target);
            throw;
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

    private static string PluginFolderPath(string gameRoot, ModEntry mod) =>
        Path.GetFullPath(Path.Combine(gameRoot,
            PluginsRelative.Replace('/', Path.DirectorySeparatorChar),
            mod.EffectivePluginFolder));

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
