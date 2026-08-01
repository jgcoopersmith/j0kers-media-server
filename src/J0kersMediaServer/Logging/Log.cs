namespace J0kersMediaServer.Logging;

public enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4 }

/// <summary>Minimal leveled console logger; level set from config at startup.</summary>
public static class Log
{
    public static LogLevel Level { get; set; } = LogLevel.Info;

    public static void SetLevel(string name) => Level = name.ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warn" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };

    public static void Trace(string area, string msg) => Write(LogLevel.Trace, area, msg);
    public static void Debug(string area, string msg) => Write(LogLevel.Debug, area, msg);
    public static void Info(string area, string msg) => Write(LogLevel.Info, area, msg);
    public static void Warn(string area, string msg) => Write(LogLevel.Warn, area, msg);
    public static void Error(string area, string msg) => Write(LogLevel.Error, area, msg);

    private static void Write(LogLevel level, string area, string msg)
    {
        if (level < Level) return;
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] [{area}] {msg}");
    }
}
