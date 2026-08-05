namespace J0kersMediaServer.Logging;

public enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4 }

/// <summary>
/// Leveled logger: always to the console, and optionally to a rotating file
/// so there is a record after the console window is gone (tray mode leaves
/// no console at all). Rotation is by age, by size, or by both — whichever
/// comes first — and old files are pruned to a fixed count.
/// </summary>
public static class Log
{
    public static LogLevel Level { get; set; } = LogLevel.Info;

    public static void SetLevel(string name) => Level = Parse(name);

    public static LogLevel Parse(string name) => name.ToLowerInvariant() switch
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
        var now = DateTime.Now;
        var tag = level.ToString().ToUpperInvariant();
        Console.WriteLine($"{now:HH:mm:ss.fff} [{tag,-5}] [{area}] {msg}");
        _file?.Write($"{now:yyyy-MM-dd HH:mm:ss.fff} [{tag,-5}] [{area}] {msg}");
    }

    // ---- file sink ----

    private static LogFile? _file;

    /// <summary>The active log file, or null when file logging is off.</summary>
    public static string? FilePath => _file?.Path;

    /// <summary>
    /// Turns the file sink on, off, or reconfigures it in place. Safe to call
    /// while the server is running — the dashboard's Config dialog does.
    /// </summary>
    public static void ConfigureFile(bool enabled, string directory, int rotateSizeMb,
                                     string rotatePeriod, int maxFiles)
    {
        var old = _file;
        _file = null;
        old?.Dispose();

        if (!enabled) return;
        try
        {
            _file = new LogFile(directory, rotateSizeMb, rotatePeriod, maxFiles);
            Info("log", $"writing to {_file.Path} (rotate: " +
                        $"{(rotateSizeMb > 0 ? rotateSizeMb + " MB" : "no size limit")}, " +
                        $"{(rotatePeriod is "none" or "" ? "no period" : rotatePeriod)}, " +
                        $"keeping {maxFiles} old file{(maxFiles == 1 ? "" : "s")})");
        }
        catch (Exception ex)
        {
            // a bad log directory must never stop the server starting
            Console.WriteLine($"[WARN ] [log] file logging disabled: {ex.Message}");
        }
    }

    /// <summary>Flushes and closes the log file (called at shutdown).</summary>
    public static void CloseFile()
    {
        var f = _file;
        _file = null;
        f?.Dispose();
    }

    /// <summary>The log files on disk, newest first: name, size, and time.</summary>
    public static IReadOnlyList<(string Name, long Bytes, DateTime Modified)> Files(string directory)
    {
        try
        {
            var dir = new DirectoryInfo(LogFile.ResolveDirectory(directory));
            if (!dir.Exists) return Array.Empty<(string, long, DateTime)>();
            return dir.GetFiles(LogFile.Stem + "*.log")
                      .OrderByDescending(f => f.LastWriteTime)
                      .Select(f => (f.Name, f.Length, f.LastWriteTime))
                      .ToArray();
        }
        catch
        {
            return Array.Empty<(string, long, DateTime)>();
        }
    }
}

/// <summary>
/// The rotating file itself. One writer, one lock: every logging call from
/// every connection thread lands here, so the append and the rotation check
/// have to be atomic with respect to each other.
/// </summary>
internal sealed class LogFile : IDisposable
{
    internal const string Stem = "j0kers";

    private readonly object _lock = new();
    private readonly string _dir;
    private readonly long _maxBytes;      // 0 = no size limit
    private readonly string _period;      // none | hourly | daily | weekly | monthly
    private readonly int _maxFiles;

    private StreamWriter? _writer;
    private long _bytes;
    private DateTime _periodStart;

    public string Path { get; }

    public LogFile(string directory, int rotateSizeMb, string rotatePeriod, int maxFiles)
    {
        _dir = ResolveDirectory(directory);
        _maxBytes = rotateSizeMb > 0 ? rotateSizeMb * 1024L * 1024L : 0;
        _period = Normalize(rotatePeriod);
        _maxFiles = Math.Max(0, maxFiles);
        Directory.CreateDirectory(_dir);
        Path = System.IO.Path.Combine(_dir, Stem + ".log");

        var existing = new FileInfo(Path);
        _bytes = existing.Exists ? existing.Length : 0;
        // an existing file carries its own age: a daily log written yesterday
        // belongs to yesterday's bucket, so the first line today rotates it
        _periodStart = PeriodStart(existing.Exists ? existing.LastWriteTime : DateTime.Now);
        Open();
        Prune();
    }

    internal static string ResolveDirectory(string directory) =>
        System.IO.Path.GetFullPath(string.IsNullOrWhiteSpace(directory) ? "logs" : directory);

    internal static string Normalize(string period) => period?.ToLowerInvariant() switch
    {
        "hourly" => "hourly",
        "daily" => "daily",
        "weekly" => "weekly",
        "monthly" => "monthly",
        _ => "none",
    };

    private void Open()
    {
        _writer = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true, // a crash is exactly when the tail matters most
        };
    }

    public void Write(string line)
    {
        lock (_lock)
        {
            if (_writer is null) return;
            var now = DateTime.Now;
            if (ShouldRotate(now, line.Length)) Rotate(now);
            _writer.WriteLine(line);
            _bytes += line.Length + Environment.NewLine.Length;
        }
    }

    private bool ShouldRotate(DateTime now, int incoming)
    {
        if (_period != "none" && PeriodStart(now) != _periodStart) return true;
        // don't rotate an empty file — that would just churn out blank archives
        if (_maxBytes > 0 && _bytes > 0 && _bytes + incoming > _maxBytes) return true;
        return false;
    }

    private void Rotate(DateTime now)
    {
        try
        {
            _writer?.Dispose();
            _writer = null;
            if (_bytes > 0)
            {
                var archive = System.IO.Path.Combine(_dir, $"{Stem}-{now:yyyyMMdd-HHmmss}.log");
                // same-second rotations would collide; a suffix is cheaper than losing one
                for (var n = 1; File.Exists(archive); n++)
                    archive = System.IO.Path.Combine(_dir, $"{Stem}-{now:yyyyMMdd-HHmmss}-{n}.log");
                File.Move(Path, archive);
            }
        }
        catch
        {
            // if the move fails (file locked by a viewer), keep appending to
            // the current file rather than losing the log entirely
        }
        _bytes = 0;
        _periodStart = PeriodStart(now);
        try { Open(); } catch { }
        Prune();
    }

    /// <summary>Keeps the newest <c>maxFiles</c> archives; deletes the rest.</summary>
    private void Prune()
    {
        try
        {
            var archives = new DirectoryInfo(_dir).GetFiles(Stem + "-*.log")
                .OrderByDescending(f => f.LastWriteTime)
                .Skip(_maxFiles)
                .ToArray();
            foreach (var f in archives)
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    private DateTime PeriodStart(DateTime t) => _period switch
    {
        "hourly" => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0),
        "daily" => t.Date,
        "weekly" => t.Date.AddDays(-(int)t.DayOfWeek),
        "monthly" => new DateTime(t.Year, t.Month, 1),
        _ => DateTime.MinValue,
    };

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
