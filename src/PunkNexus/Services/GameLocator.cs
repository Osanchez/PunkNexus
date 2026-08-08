using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PunkNexus.Services;

/// <summary>One checked property of a candidate folder, shown to the user during setup.</summary>
public sealed record VerificationCheck(string Label, bool Passed, bool Required);

public sealed record GameVerification(
    string Path,
    bool IsValid,
    bool IsWritable,
    IReadOnlyList<VerificationCheck> Checks,
    string? Problem)
{
    public IEnumerable<VerificationCheck> Failures => Checks.Where(c => !c.Passed);
}

/// <summary>
/// Finds and validates the PUNK install. Detection is best-effort and always overridable; the
/// verification below is what actually gates installing, so a hand-picked folder is held to the
/// same standard as a detected one.
/// </summary>
public static class GameLocator
{
    public const string SteamAppId = "2850470";
    public const string GameExe = "Punk.exe";
    private const string DataDir = "Punk_Data";

    /// <summary>
    /// Verifies a folder really is a PUNK install. Required checks decide validity; the rest are
    /// reported so a near-miss (e.g. the right game, wrong subfolder) is diagnosable at a glance.
    /// </summary>
    public static GameVerification Verify(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new GameVerification("", false, false, Array.Empty<VerificationCheck>(),
                "No folder selected.");

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return new GameVerification(path, false, false, Array.Empty<VerificationCheck>(),
                $"That path is not usable: {ex.Message}");
        }

        if (!Directory.Exists(full))
            return new GameVerification(full, false, false, Array.Empty<VerificationCheck>(),
                "That folder does not exist.");

        var checks = new List<VerificationCheck>
        {
            new($"{GameExe} is present", File.Exists(Path.Combine(full, GameExe)), true),
            new($"{DataDir}\\ is present", Directory.Exists(Path.Combine(full, DataDir)), true),
            new("UnityPlayer.dll is present", File.Exists(Path.Combine(full, "UnityPlayer.dll")), false),
            new($"{DataDir}\\Managed\\ is present",
                Directory.Exists(Path.Combine(full, DataDir, "Managed")), false),
            new($"{DataDir}\\globalgamemanagers is present",
                File.Exists(Path.Combine(full, DataDir, "globalgamemanagers")), false),
        };

        var writable = IsWritable(full);
        checks.Add(new VerificationCheck("Folder is writable", writable, true));

        var isValid = checks.Where(c => c.Required).All(c => c.Passed);

        string? problem = null;
        if (!isValid)
        {
            if (!File.Exists(Path.Combine(full, GameExe)))
                problem = $"No {GameExe} here. Pick the folder that contains {GameExe} — in Steam: " +
                          "PUNK Playtest \u2192 Manage \u2192 Browse local files.";
            else if (!Directory.Exists(Path.Combine(full, DataDir)))
                problem = $"Found {GameExe} but no {DataDir}\\ folder, so this looks like a shortcut " +
                          "or a partial copy rather than the install.";
            else if (!writable)
                problem = "This folder is not writable by PUNK Nexus. Close the game if it is running, " +
                          "or relaunch PUNK Nexus as administrator.";
        }

        return new GameVerification(full, isValid, writable, checks, problem);
    }

    /// <summary>
    /// Cheap "is the install still there" test, safe to run on a timer. Deliberately skips the
    /// write probe so polling does not touch the disk.
    /// </summary>
    public static bool QuickCheck(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path!, GameExe)) &&
        Directory.Exists(Path.Combine(path!, DataDir));

    /// <summary>
    /// Probe rather than infer. Folder ACLs, read-only volumes and virtualization all make a path
    /// look writable when it is not, and the failure would otherwise surface halfway through an
    /// install with files already on disk.
    /// </summary>
    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".punknexus-write-test-{Guid.NewGuid():N}");
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       1, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Every plausible install folder, best guess first, de-duplicated and verified.</summary>
    public static IReadOnlyList<string> FindCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hits = new List<string>();

        void Consider(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            string full;
            try { full = Path.GetFullPath(candidate); } catch { return; }
            if (!seen.Add(AppPaths.Normalize(full))) return;
            if (Verify(full).IsValid) hits.Add(full);
        }

        foreach (var library in SteamLibraries())
        {
            var steamapps = Path.Combine(library, "steamapps");

            // The app manifest names the install folder exactly — the reliable path.
            foreach (var installDir in AppManifestInstallDirs(steamapps))
                Consider(Path.Combine(steamapps, "common", installDir));

            // Fall back to scanning the library, which also catches a renamed folder.
            var common = Path.Combine(steamapps, "common");
            if (Directory.Exists(common))
            {
                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(common); } catch { continue; }
                foreach (var dir in subdirs)
                {
                    var name = Path.GetFileName(dir);
                    if (name.Contains("punk", StringComparison.OrdinalIgnoreCase))
                        Consider(dir);
                }
            }
        }

        return hits;
    }

    /// <summary>Steam library roots: the install itself plus every extra library folder.</summary>
    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();

        foreach (var steam in SteamRoots())
        {
            roots.Add(steam);

            // libraryfolders.vdf lists the other drives. Its shape has changed across Steam
            // versions, so pull every "path" value rather than parsing the structure.
            foreach (var vdf in new[]
                     {
                         Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
                         Path.Combine(steam, "config", "libraryfolders.vdf"),
                     })
            {
                if (!File.Exists(vdf)) continue;
                string text;
                try { text = File.ReadAllText(vdf); } catch { continue; }

                foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"",
                             RegexOptions.IgnoreCase))
                {
                    var p = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (!string.IsNullOrWhiteSpace(p)) roots.Add(p);
                }
            }
        }

        return roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SteamRoots()
    {
        var found = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            foreach (var (root, key, value) in new (RegistryKey, string, string)[]
                     {
                         (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                         (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                         (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
                     })
            {
                try
                {
                    using var sub = root.OpenSubKey(key);
                    if (sub?.GetValue(value) is string s && !string.IsNullOrWhiteSpace(s))
                        found.Add(s.Replace('/', '\\'));
                }
                catch
                {
                    // A missing key or a locked hive is normal; the fixed paths below still apply.
                }
            }

            foreach (var drive in DriveLetters())
            {
                found.Add($@"{drive}:\Program Files (x86)\Steam");
                found.Add($@"{drive}:\Program Files\Steam");
                found.Add($@"{drive}:\Steam");
                found.Add($@"{drive}:\SteamLibrary");
                found.Add($@"{drive}:\Games\Steam");
            }
        }
        else
        {
            // Not a supported target, but it keeps the locator exercisable off Windows.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            found.Add(Path.Combine(home, ".steam", "steam"));
            found.Add(Path.Combine(home, ".local", "share", "Steam"));
        }

        return found;
    }

    private static IEnumerable<char> DriveLetters()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return "CD".ToCharArray(); }

        return drives
            .Where(d => d.Name.Length > 0 && char.IsLetter(d.Name[0]))
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .Distinct();
    }

    private static IEnumerable<string> AppManifestInstallDirs(string steamapps)
    {
        var acf = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
        if (!File.Exists(acf)) yield break;

        string text;
        try { text = File.ReadAllText(acf); }
        catch { yield break; }

        var m = Regex.Match(text, "\"installdir\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        if (m.Success) yield return m.Groups[1].Value;
    }
}
