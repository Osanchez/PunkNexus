using System.Text;
using System.Text.RegularExpressions;

namespace PunkNexus.Services;

/// <summary>What the client could learn about the installed build.</summary>
public sealed record GameBuild(string? Version, string? SteamBuildId, string? SteamAppId)
{
    public bool HasVersion => !string.IsNullOrWhiteSpace(Version);

    public string Display => HasVersion
        ? (string.IsNullOrWhiteSpace(SteamBuildId) ? Version! : $"{Version} (build {SteamBuildId})")
        : "unknown";

    public static readonly GameBuild Unknown = new(null, null, null);
}

/// <summary>
/// Reads the installed game's version out of the install itself, rather than trusting anything the
/// catalogue claims. Compatibility is gated on this, so it has to come from the user's own disk.
/// </summary>
public static class GameVersionDetector
{
    /// <summary>PlayerSettings sits near the top of the file; no need to scan a 50 MB blob.</summary>
    private const int ScanLimit = 262_144;

    private static readonly Regex DottedNumeric =
        new(@"^[0-9]+(\.[0-9]+)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static GameBuild Detect(string gameRoot)
    {
        var version = ReadBundleVersion(gameRoot);
        var (appId, buildId) = ReadSteamBuild(gameRoot);
        var build = new GameBuild(version, buildId, appId);

        Log.Info($"Detected game build: {build.Display}");
        return build;
    }

    /// <summary>
    /// Unity bakes PlayerSettings into <c>Punk_Data/globalgamemanagers</c>. Serialized strings there
    /// are length-prefixed (int32 LE) and padded to a 4-byte boundary. Walk them and take the
    /// dotted-numeric string with the most components — that is bundleVersion ("0.12.10"), which
    /// beats the per-platform build numbers like "1.0". The engine version ("6000.3.4f1") is
    /// skipped for free because it is not purely digits and dots.
    /// </summary>
    private static string? ReadBundleVersion(string gameRoot)
    {
        var path = Path.Combine(gameRoot, "Punk_Data", "globalgamemanagers");
        if (!File.Exists(path)) return null;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read {path}: {ex.Message}");
            return null;
        }

        var scanEnd = Math.Min(bytes.Length, ScanLimit);
        string? best = null;
        var bestParts = -1;
        var bestPos = -1;

        var i = 0;
        while (i < scanEnd - 4)
        {
            var len = BitConverter.ToInt32(bytes, i);
            if (len is >= 1 and <= 64 && i + 4 + len <= bytes.Length)
            {
                var printable = true;
                for (var j = 0; j < len; j++)
                {
                    var c = bytes[i + 4 + j];
                    if (c < 32 || c > 126) { printable = false; break; }
                }

                if (printable)
                {
                    var s = Encoding.ASCII.GetString(bytes, i + 4, len);
                    if (DottedNumeric.IsMatch(s))
                    {
                        var parts = s.Split('.').Length;
                        if (parts > bestParts || (parts == bestParts && i > bestPos))
                        {
                            best = s;
                            bestParts = parts;
                            bestPos = i;
                        }
                    }

                    var advance = 4 + len;
                    i += advance + (4 - advance % 4) % 4;
                    continue;
                }
            }

            i++;
        }

        if (best is null) Log.Warn($"Could not read a bundleVersion from {path}.");
        return best;
    }

    /// <summary>
    /// The Steam app manifest two directories up carries the immutable build id. Matched by
    /// installdir rather than a hardcoded appid, so a renamed or non-Steam copy simply yields null.
    /// </summary>
    private static (string? AppId, string? BuildId) ReadSteamBuild(string gameRoot)
    {
        try
        {
            var full = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar);
            var leaf = Path.GetFileName(full);

            // ...\steamapps\common\<game>  ->  ...\steamapps
            var common = Path.GetDirectoryName(full);
            var steamapps = common is null ? null : Path.GetDirectoryName(common);
            if (steamapps is null || !Directory.Exists(steamapps)) return (null, null);

            foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(acf); } catch { continue; }

                var installDir = Match(text, "installdir");
                if (!string.Equals(installDir, leaf, StringComparison.OrdinalIgnoreCase)) continue;

                return (Match(text, "appid"), Match(text, "buildid"));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the Steam app manifest: {ex.Message}");
        }

        return (null, null);
    }

    private static string? Match(string text, string key)
    {
        var m = Regex.Match(text, $"\"{key}\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
