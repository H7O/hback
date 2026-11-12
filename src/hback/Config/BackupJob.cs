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

    /// <summary>Where the files go. Every destination gets all of them. There is at least one.</summary>
    public required IReadOnlyList<DestinationSpec> Destinations { get; init; }

    /// <summary>How many more times a run that could not reach a destination is tried.</summary>
    public int Retries { get; init; } = 3;

    public int RetryWaitMinutes { get; init; } = 5;

    /// <summary>False for an entry without a <c>&lt;sys&gt;</c> element, which only runs when asked to.</summary>
    public bool Scheduled { get; init; }

    /// <summary>The schedule in words, for <c>hback status</c>.</summary>
    public string ScheduleText { get; init; } = "";

    /// <summary>
    /// What a run to one destination is called in the log: the backup's name, and the destination
    /// as well when the backup has more than one.
    /// </summary>
    public string LabelFor(DestinationSpec destination)
        => Destinations.Count == 1 ? Name : $"{Name} ({destination.Label})";

    /// <summary>
    /// The path a file or folder of this machine has at a destination, or null when it is not under
    /// any source of this backup.
    /// </summary>
    public string? BackupPathOf(DestinationSpec destination, string fullPath)
    {
        fullPath = PathText.TrimEndSeparators(Path.GetFullPath(fullPath));
        if (destination.SourcePath == SourcePathStyle.Full)
            return PathText.ToBackupPath(fullPath);

        // The longest source that holds it: with "C:\code" and "C:\code\app" both listed, a file in
        // the second is looked for where the second put it.
        foreach (var source in Sources.OrderByDescending(s => s.Length))
        {
            if (fullPath.Equals(source, PathComparison))
                return File.Exists(source) ? PathText.FilePath(destination.SourcePath, source) : PathText.FolderPrefix(destination.SourcePath, source);

            var top = source.EndsWith(Path.DirectorySeparatorChar) ? source : source + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(top, PathComparison))
                return PathText.Join(PathText.FolderPrefix(destination.SourcePath, source), PathText.ToMatchPath(fullPath[top.Length..]));
        }
        return null;
    }

    public static BackupJob From(IHTaskItem task)
    {
        string? Get(string key) => Clean(task.GetItem(key)?.GetValue());

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

        var destinations = ReadDestinations(task, name, Get);

        // With only its own name kept, two sources of the same name would share one folder.
        if (destinations.Any(d => d.SourcePath == SourcePathStyle.Name)
            && sources.GroupBy(s => PathText.NameOf(PathText.ToBackupPath(s)), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twins)
            throw new ConfigException($"Backup '{name}': with <source_path>name</source_path> the sources '{twins.First()}' and '{twins.Skip(1).First()}' " +
                $"would both go into the folder '{twins.Key}'. Use full, or give each its own backup.");

        return new BackupJob
        {
            Name = name,
            Sources = sources,
            Excludes = PathText.Lines(Get("exclude")),
            Destinations = destinations,
            Retries = Number(Get("retries"), 3, 0, 100, name, "retries"),
            RetryWaitMinutes = Number(Get("retry_wait_minutes"), 5, 0, 1440, name, "retry_wait_minutes"),
            Scheduled = task.Schedule is not null,
            ScheduleText = DescribeSchedule(task.Schedule),
        };
    }

    /// <summary>
    /// The destinations of a backup. They can be written three ways, which may be mixed:
    /// a <c>&lt;destination&gt;</c> element holding an address, repeated for each place;
    /// a <c>&lt;destination&gt;</c> element holding a <c>&lt;path&gt;</c> and settings of its own;
    /// and a <c>&lt;destinations&gt;</c> element that either lists addresses one per line, as
    /// <c>&lt;sources&gt;</c> does, or holds <c>&lt;destination&gt;</c> elements.
    /// A setting that a destination does not have itself is taken from the backup.
    /// </summary>
    private static List<DestinationSpec> ReadDestinations(IHTaskItem task, string name, Func<string, string?> fromBackup)
    {
        var found = new List<DestinationSpec>();

        void Add(string address, Func<string, string?> get, string? label)
        {
            try
            {
                found.Add(DestinationSpec.Parse(address, get) with { Name = label });
            }
            catch (ConfigException ex)
            {
                throw new ConfigException($"Backup '{name}': {ex.Message}");
            }
        }

        void AddElement(IHTaskItem element)
        {
            if (element.Children is not { Count: > 0 })
            {
                Add(Clean(element.GetValue()) ?? throw new ConfigException($"Backup '{name}' has an empty <destination>."), fromBackup, null);
                return;
            }

            // An address written beside the settings would otherwise be passed over without a word.
            if (HasTextOfItsOwn(element))
                throw new ConfigException($"Backup '{name}': a <destination> with settings of its own gives its address in a <path>. " +
                    "This one has text beside its settings, which would not be read.");

            string? Own(string key) => Clean(element.GetItem(key)?.GetValue());
            Add(Own("path") ?? throw new ConfigException($"Backup '{name}': a <destination> with settings of its own needs a <path>, the folder or sftp:// address."),
                key => Own(key) ?? fromBackup(key), Own("name"));
        }

        foreach (var element in task.GetItems("destination") ?? [])
            if (element is not null) AddElement(element);

        if (task.GetItem("destinations") is { } list)
        {
            var elements = (list.Children ?? []).Where(e => e is not null).ToList();
            if (elements.Count == 0)
            {
                foreach (var line in PathText.Lines(Clean(list.GetValue())))
                    Add(line, fromBackup, null);
            }
            else
            {
                if (elements.FirstOrDefault(e => !e!.Name.Equals("destination", StringComparison.OrdinalIgnoreCase)) is { } stranger)
                    throw new ConfigException($"Backup '{name}': <destinations> holds a <{stranger.Name}>. Inside it, list one address per line, or write each destination as a <destination>.");
                // Lines beside the elements can't be told apart from the elements' own text with any
                // certainty, so they are not half-read: the list is one or the other.
                if (HasTextOfItsOwn(list))
                    throw new ConfigException($"Backup '{name}': <destinations> has lines of text beside its <destination> elements. " +
                        "Write every destination in it as a <destination> element, or all of them as lines.");
                foreach (var element in elements)
                    AddElement(element!);
            }
        }

        if (found.Count == 0)
            throw new ConfigException($"Backup '{name}' has no <destination>.");

        for (var i = 0; i < found.Count; i++)
            for (var j = i + 1; j < found.Count; j++)
            {
                if (found[i].SharesFoldersWith(found[j]))
                    throw new ConfigException($"Backup '{name}' lists the destination {found[i].Display} twice.");
                if (found[i].Name is { } label && label.Equals(found[j].Name, StringComparison.OrdinalIgnoreCase))
                    throw new ConfigException($"Backup '{name}' has two destinations named '{label}'. Give each its own <name>.");
            }
        return found;
    }

    /// <summary>
    /// Whether an element holds text itself, beside what its child elements hold.
    /// <para>
    /// The scheduler's reader gives an element's text with its children's text run in. Where one ends
    /// and the other begins can't be found by searching: a child's text may well occur in the
    /// element's own, as "D:\site" does in "D:\site2". How much there is of each can be counted,
    /// though, and counting is exact. If the element has more letters than its children have between
    /// them, the rest are its own.
    /// </para>
    /// </summary>
    private static bool HasTextOfItsOwn(IHTaskItem element)
    {
        static int Letters(string? text) => (text ?? "").Count(c => !char.IsWhiteSpace(c));
        return Letters(element.RawValue) > (element.Children ?? []).Sum(child => Letters(child?.RawValue));
    }

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

    /// <summary>Paths differ by letter case on Linux, and don't on Windows and macOS.</summary>
    public static StringComparer PathComparer
        => OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static StringComparison PathComparison
        => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static string? Clean(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int Number(string? text, int fallback, int min, int max, string backup, string tag)
    {
        if (text is null) return fallback;
        if (!int.TryParse(text, out var n) || n < min || n > max)
            throw new ConfigException($"Backup '{backup}': <{tag}> must be a number from {min} to {max}.");
        return n;
    }
}
