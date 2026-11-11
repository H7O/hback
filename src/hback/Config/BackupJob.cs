using Com.H.Threading.Scheduler;

namespace HBack.Config;

/// <summary>A mistake in hback.xml, worded for the person who wrote it.</summary>
public sealed class ConfigException(string message) : Exception(message);

/// <summary>
/// One <c>&lt;backup&gt;</c> entry of hback.xml: what to copy, what to leave out, and where it goes.
/// When it runs is the scheduler's business (the entry's <c>&lt;sys&gt;</c> element), not this class's.
/// </summary>
public sealed record BackupJob
{
    public required string Name { get; init; }

    /// <summary>Full paths of the folders and files to back up.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>Exclude patterns, one per entry, in the style of a .gitignore line.</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];

    public required DestinationSpec Destination { get; init; }

    /// <summary>
    /// How long the previous copy of a changed or deleted file is kept at the destination.
    /// Zero keeps nothing: a changed file is overwritten and a deleted file is removed.
    /// </summary>
    public int KeepHistoryDays { get; init; } = 30;

    /// <summary>How many files are copied at the same time. Each one uses its own SFTP connection.</summary>
    public int Parallel { get; init; } = 4;

    /// <summary>How many more times a run that could not reach the destination is tried.</summary>
    public int Retries { get; init; } = 3;

    public int RetryWaitMinutes { get; init; } = 5;

    /// <summary>False for an entry without a <c>&lt;sys&gt;</c> element, which only runs when asked to.</summary>
    public bool Scheduled { get; init; }

    /// <summary>The schedule in words, for <c>hback status</c>.</summary>
    public string ScheduleText { get; init; } = "";

    public static BackupJob From(IHTaskItem task)
        => From(key => task.GetItem(key)?.GetValue(), task.Schedule is not null) with { ScheduleText = DescribeSchedule(task.Schedule) };

    /// <summary>"every 30 min", "once a day at 01:00", worded from the scheduler's own reading of the entry.</summary>
    private static string DescribeSchedule(IHTaskControlProperties? schedule)
    {
        if (schedule is null) return "none (runs only with 'hback run')";
        var text = schedule.Interval is { } ms
            ? "every " + Engine.Text.Duration(TimeSpan.FromMilliseconds(ms))
            : "once a day";
        if (schedule.Time is { } time)
            text += schedule.Interval is null ? $" at {time:hh\\:mm}" : $", from {time:hh\\:mm}";
        if (schedule.UntilTime is { } until) text += $" until {until:hh\\:mm}";
        if (schedule.DaysOfWeek is { } days) text += " on " + string.Join(", ", days);
        if (!schedule.Enabled) text += " (disabled)";
        return text;
    }

    public static BackupJob From(Func<string, string?> read, bool scheduled = true)
    {
        string? Get(string key)
        {
            var v = read(key)?.Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }

        var name = Get("name") ?? throw new ConfigException("A backup has no <name>.");

        var sources = new List<string>();
        foreach (var line in PathText.Lines(Get("sources")))
        {
            var path = PathText.Expand(line);
            if (!Path.IsPathFullyQualified(path))
                throw new ConfigException($"Backup '{name}': the source '{line}' must be a full path.");
            path = PathText.TrimEndSeparators(Path.GetFullPath(path));
            if (!sources.Contains(path, PathComparer))
                sources.Add(path);
        }
        if (sources.Count == 0)
            throw new ConfigException($"Backup '{name}' has no <sources>. List one folder or file per line.");

        var destination = Get("destination")
            ?? throw new ConfigException($"Backup '{name}' has no <destination>.");

        DestinationSpec spec;
        try
        {
            spec = DestinationSpec.Parse(destination, Get);
        }
        catch (ConfigException ex)
        {
            throw new ConfigException($"Backup '{name}': {ex.Message}");
        }

        return new BackupJob
        {
            Name = name,
            Sources = sources,
            Excludes = PathText.Lines(Get("exclude")),
            Destination = spec,
            KeepHistoryDays = Number(Get("keep_history_days"), 30, 0, 36500, name, "keep_history_days"),
            Parallel = Number(Get("parallel"), 4, 1, 16, name, "parallel"),
            Retries = Number(Get("retries"), 3, 0, 100, name, "retries"),
            RetryWaitMinutes = Number(Get("retry_wait_minutes"), 5, 0, 1440, name, "retry_wait_minutes"),
            Scheduled = scheduled,
        };
    }

    /// <summary>Paths differ by letter case on Linux, and don't on Windows and macOS.</summary>
    public static StringComparer PathComparer
        => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static StringComparison PathComparison
        => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static int Number(string? text, int fallback, int min, int max, string backup, string tag)
    {
        if (text is null) return fallback;
        if (!int.TryParse(text, out var n) || n < min || n > max)
            throw new ConfigException($"Backup '{backup}': <{tag}> must be a number from {min} to {max}.");
        return n;
    }
}
