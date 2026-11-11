using Com.H.Extensions.Logging.File;

namespace HBack;

public static class Logging
{
    /// <summary>
    /// hback logs to the console in plain lines, and to a file per day under the data folder
    /// (logs/YYYY/MM/DD/log.txt, kept for 30 days) through Com.H.Extensions.Logging.File.
    /// </summary>
    public static void Configure(ILoggingBuilder logging, AppPaths paths, bool verbose)
    {
        logging.ClearProviders();
        logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddProvider(new PlainConsoleLoggerProvider(Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService()));
        logging.AddProvider(new SimpleFileLoggerProvider(paths.LogDir, 30));
    }

    public static ILoggerFactory Create(AppPaths paths, bool verbose)
        => LoggerFactory.Create(logging => Configure(logging, paths, verbose));
}

/// <summary>
/// Writes "12:30:05 message" lines. The stock console logger puts a category and an event id in front
/// of every line, which is noise for a tool a person runs by hand.
/// <para>
/// Under systemd the lines go to the journal, which stamps the time itself. There the time is left
/// out and each line starts with its syslog level ("&lt;4&gt;" for a warning), which the journal reads
/// and removes, so <c>journalctl -p warning</c> finds the lines that matter.
/// </para>
/// </summary>
public sealed class PlainConsoleLoggerProvider(bool forJournal = false) : ILoggerProvider
{
    private static readonly Lock Gate = new();
    private readonly PlainConsoleLogger _logger = new(forJournal);

    public ILogger CreateLogger(string categoryName) => _logger;
    public void Dispose() { }

    private sealed class PlainConsoleLogger(bool forJournal) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (forJournal)
            {
                var level = logLevel switch
                {
                    >= LogLevel.Error => 3,
                    LogLevel.Warning => 4,
                    LogLevel.Information => 6,
                    _ => 7,
                };
                // A line break would start a new journal entry without a level, so each line gets its own.
                var text = exception is not null && logLevel >= LogLevel.Error ? message + Environment.NewLine + exception : message;
                lock (Gate)
                    foreach (var line in text.Split('\n'))
                        Console.WriteLine($"<{level}>{line.TrimEnd('\r')}");
                return;
            }

            var prefix = logLevel switch
            {
                LogLevel.Warning => "warning: ",
                >= LogLevel.Error => "error: ",
                _ => "",
            };
            var color = logLevel switch
            {
                LogLevel.Warning => ConsoleColor.Yellow,
                >= LogLevel.Error => ConsoleColor.Red,
                LogLevel.Debug or LogLevel.Trace => ConsoleColor.DarkGray,
                _ => (ConsoleColor?)null,
            };

            lock (Gate)
            {
                var colored = color is not null && !Console.IsOutputRedirected;
                if (colored) Console.ForegroundColor = color!.Value;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} {prefix}{message}");
                if (exception is not null && logLevel >= LogLevel.Error)
                    Console.WriteLine(exception);
                if (colored) Console.ResetColor();
            }
        }
    }
}
