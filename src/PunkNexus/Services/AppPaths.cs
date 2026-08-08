using System.Security.Cryptography;
using System.Text;

namespace PunkNexus.Services;

/// <summary>Where the client keeps its own files. Never inside the game folder.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create),
        "PunkNexus");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string CacheDir => Path.Combine(Root, "cache");
    public static string IconCacheDir => Path.Combine(CacheDir, "icons");
    public static string InstallsDir => Path.Combine(Root, "installs");
    public static string DownloadsDir => Path.Combine(Root, "downloads");
    public static string LogFile => Path.Combine(Root, "punknexus.log");

    /// <summary>Install state file for one game folder, keyed by a hash of its normalized path.</summary>
    public static string InstallStateFile(string gameRoot)
    {
        var key = Normalize(gameRoot);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(InstallsDir, $"{hash}.json");
    }

    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .ToLowerInvariant();
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(IconCacheDir);
        Directory.CreateDirectory(InstallsDir);
        Directory.CreateDirectory(DownloadsDir);
    }
}
