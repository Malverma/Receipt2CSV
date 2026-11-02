namespace POSscan.App;

/// <summary>Plain text log for support. Never throws: logging must not be able to take the app down.</summary>
public static class Log
{
    public static string Folder { get; set; } = Path.Combine(Paths.DataDir, "logs");

    public static void Error(string context, Exception ex) => Write($"ERROR {context}: {ex}");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, $"posscan-{DateTime.Now:yyyy-MM-dd}.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine(message);
        }
    }
}

public static class Paths
{
    public static string DataDir =>
        Environment.GetEnvironmentVariable("POSSCAN_DATA") is { Length: > 0 } custom ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "POSscan");
}
