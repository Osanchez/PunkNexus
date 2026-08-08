namespace PunkNexus.Services;

/// <summary>
/// Small append-only log next to the settings file. When an install fails on someone else's
/// machine this is the only forensic trail, so every service writes its failures here.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Root);

                // Keep the log from growing without bound across many sessions.
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 1_000_000)
                    File.Move(AppPaths.LogFile, AppPaths.LogFile + ".old", overwrite: true);

                File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never be the thing that breaks the app.
        }

        Console.WriteLine(line);
    }
}
