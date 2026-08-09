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

    /// <summary>
    /// Log a failure with enough to diagnose it from the file alone.
    ///
    /// Type and message by themselves are often useless: an EntryPointNotFoundException from a
    /// P/Invoke says only "Entry point was not found", and the name of the function it wanted is
    /// in the stack trace. A user's log is usually the only evidence anyone gets, so it carries
    /// the trace and any inner exceptions.
    /// </summary>
    public static void Error(string message, Exception ex)
    {
        var text = $"{message}: {ex.GetType().Name}: {ex.Message}";
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            text += Environment.NewLine + $"  caused by {inner.GetType().Name}: {inner.Message}";
        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            text += Environment.NewLine + ex.StackTrace;
        Write("ERROR", text);
    }

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
