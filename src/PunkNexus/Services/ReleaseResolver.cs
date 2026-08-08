using System.Text.Json;
using System.Text.RegularExpressions;
using PunkNexus.Models;

namespace PunkNexus.Services;

public sealed record ResolvedAsset(string Url, string FileName, string? Version, long SizeBytes);

/// <summary>
/// Turns an <see cref="AssetSource"/> into a concrete download.
///
/// Mod zips are published as <c>&lt;Mod&gt;-v&lt;version&gt;.zip</c>, so the manifest cannot hold a
/// fixed URL without going stale on every version bump. Instead it holds a glob, and the latest
/// release's asset list is matched against it — which also yields the true current version, so the
/// UI shows what is actually downloadable rather than what the manifest last claimed.
/// </summary>
public sealed class ReleaseResolver
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, Task<IReadOnlyList<ReleaseAsset>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ReleaseResolver(HttpClient http) => _http = http;

    public sealed record ReleaseAsset(string Name, string Url, long Size);

    /// <summary>Drops memoized release data so a manual refresh really re-checks GitHub.</summary>
    public void Invalidate()
    {
        lock (_gate) _cache.Clear();
    }

    public async Task<ResolvedAsset?> ResolveAsync(AssetSource? source, CancellationToken ct)
    {
        if (source is null) return null;

        if (!string.IsNullOrWhiteSpace(source.Url))
        {
            var name = Path.GetFileName(new Uri(source.Url).AbsolutePath);
            return new ResolvedAsset(source.Url, name, null, 0);
        }

        if (string.IsNullOrWhiteSpace(source.Repo) || string.IsNullOrWhiteSpace(source.AssetPattern))
            return null;

        var assets = await GetLatestAssetsAsync(source.Repo!, ct).ConfigureAwait(false);
        var regex = GlobToRegex(source.AssetPattern!);

        ResolvedAsset? best = null;
        Version? bestVersion = null;

        foreach (var asset in assets)
        {
            var m = regex.Match(asset.Name);
            if (!m.Success) continue;

            var versionText = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : null;
            var candidate = new ResolvedAsset(asset.Url, asset.Name, versionText, asset.Size);

            // A release normally carries one asset per mod, but pick the highest version if the
            // pattern ever matches several.
            if (best is null)
            {
                best = candidate;
                Version.TryParse(versionText, out bestVersion);
                continue;
            }

            if (Version.TryParse(versionText, out var v) && bestVersion is not null && v > bestVersion)
            {
                best = candidate;
                bestVersion = v;
            }
        }

        return best;
    }

    private Task<IReadOnlyList<ReleaseAsset>> GetLatestAssetsAsync(string repo, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(repo, out var cached)) return cached;
            var task = FetchLatestAssetsAsync(repo, ct);
            _cache[repo] = task;
            return task;
        }
    }

    private async Task<IReadOnlyList<ReleaseAsset>> FetchLatestAssetsAsync(string repo, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{repo}/releases/latest";

            // Scoped to this request: it is the only call in the app that talks to the GitHub API.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var list = new List<ReleaseAsset>();
            if (doc.RootElement.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var link = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(link))
                        list.Add(new ReleaseAsset(name!, link!, size));
                }
            }

            Log.Info($"Resolved {list.Count} release asset(s) for {repo}.");
            return list;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read the latest release of {repo}", ex);
            return Array.Empty<ReleaseAsset>();
        }
    }

    /// <summary>
    /// <c>Mod-v*.zip</c> becomes <c>^Mod\-v(.+?)\.zip$</c> — the single wildcard is captured so it
    /// doubles as the version.
    /// </summary>
    internal static Regex GlobToRegex(string glob)
    {
        var parts = glob.Split('*');
        var pattern = "^" + string.Join("(.+?)", parts.Select(Regex.Escape)) + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
