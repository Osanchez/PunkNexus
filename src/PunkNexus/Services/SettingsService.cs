using System.Text.Json;
using PunkNexus.Models;

namespace PunkNexus.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            // Corrupt settings must never block startup — the setup screen can rebuild them.
            Log.Warn($"Could not read settings, starting fresh: {ex.Message}");
            Current = new AppSettings();
        }

        if (string.IsNullOrWhiteSpace(Current.ManifestBaseUrl))
            Current.ManifestBaseUrl = AppSettings.DefaultManifestBaseUrl;

        return Current;
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not save settings: {ex.Message}");
        }
    }
}
