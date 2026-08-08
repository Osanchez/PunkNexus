using System.IO.Compression;

namespace PunkNexus.Services;

/// <summary>
/// Archive extraction that cannot write outside the target folder. Everything here lands in the
/// user's game install, so a crafted entry name like <c>../../Windows/System32/x.dll</c> must be
/// rejected rather than followed. Entries are all validated before the first byte is written, so a
/// bad archive leaves nothing behind.
/// </summary>
public static class ZipSafe
{
    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="destRoot"/>, overwriting existing
    /// files. Returns every file written, relative to the root and '/'-separated.
    /// </summary>
    public static IReadOnlyList<string> Extract(
        string zipPath,
        string destRoot,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var root = Path.GetFullPath(destRoot);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);

        // Plan and validate the whole archive first — a rejected entry must abort before any write.
        var planned = new List<(ZipArchiveEntry Entry, string FullPath, string Relative)>();
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // Directory entries have an empty Name; the directories get created from file paths.
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (relative.Length == 0) continue;

            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Archive entry '{entry.FullName}' would write outside the game folder. " +
                    "The download was rejected and nothing was installed.");

            planned.Add((entry, full, relative));
        }

        var written = new List<string>(planned.Count);
        for (var i = 0; i < planned.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (entry, full, relative) = planned[i];

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, overwrite: true);
            written.Add(relative);

            progress?.Report((i + 1) / (double)planned.Count);
        }

        return written;
    }

    /// <summary>
    /// Reads one text entry out of an archive without extracting anything, so the contents can be
    /// inspected before a single file is committed to the game folder. Path match is
    /// case-insensitive and separator-agnostic, since archives are written on every platform.
    /// </summary>
    public static string? TryReadTextEntry(string zipPath, string relativePath)
    {
        var wanted = relativePath.Replace('\\', '/').Trim('/');

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/').Trim('/');
                if (!name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read '{relativePath}' from {zipPath}: {ex.Message}");
        }

        return null;
    }

    /// <summary>Lists the file entries in an archive without extracting, for a pre-install preview.</summary>
    public static IReadOnlyList<string> ListFiles(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        return archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Select(e => e.FullName.Replace('\\', '/'))
            .ToList();
    }
}
