using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace PunkNexus.Services;

/// <summary>
/// Fetches and caches mod icons. Every failure path returns null — the list falls back to a
/// generated monogram tile, so a dead icon URL never shows as a broken image.
/// </summary>
public sealed class IconCache
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, Bitmap?> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IconCache(HttpClient http) => _http = http;

    public async Task<Bitmap?> GetAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_memory.TryGetValue(url!, out var cached)) return cached;
        }
        finally
        {
            _gate.Release();
        }

        var bitmap = await LoadAsync(url!, ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _memory[url!] = bitmap;
        }
        finally
        {
            _gate.Release();
        }

        return bitmap;
    }

    private async Task<Bitmap?> LoadAsync(string url, CancellationToken ct)
    {
        var file = Path.Combine(AppPaths.IconCacheDir, CacheKey(url));

        try
        {
            if (File.Exists(file))
                return new Bitmap(file);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cached icon {file} is unreadable: {ex.Message}");
            try { File.Delete(file); } catch { /* best effort */ }
        }

        try
        {
            var bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);

            // Guard against a URL that points at something large and non-image.
            if (bytes.Length is 0 or > 4 * 1024 * 1024) return null;

            Directory.CreateDirectory(AppPaths.IconCacheDir);
            await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);

            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Could not load icon {url}: {ex.Message}");
            return null;
        }
    }

    private static string CacheKey(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24];
        return hash + ".img";
    }
}
