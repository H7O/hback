using System.Globalization;

namespace HBack.Engine;

public enum RunStatus
{
    /// <summary>Everything that needed copying was copied.</summary>
    Ok,
    /// <summary>The run finished, but some files or folders could not be read or copied.</summary>
    Errors,
    /// <summary>The run could not do its work, usually because the destination was out of reach.</summary>
    Failed,
    /// <summary>hback was stopped while the run was going.</summary>
    Cancelled,
    /// <summary>Another run was already writing to the same destination.</summary>
    Skipped,
}

public sealed class RunSummary
{
    public required string Backup { get; init; }
    public RunStatus Status { get; set; }
    public int Scanned { get; set; }
    public int Copied { get; set; }
    public long CopiedBytes { get; set; }
    public int Removed { get; set; }
    public int Failed { get; set; }

    /// <summary>Files whose time changed but whose content was the same, so nothing was sent.</summary>
    public int SameContent { get; set; }

    /// <summary>Files found already at the destination when hback had no record of it.</summary>
    public int Adopted { get; set; }

    /// <summary>Sources and folders that could not be read.</summary>
    public int Unreadable { get; set; }

    public string? Message { get; set; }
    public TimeSpan Elapsed { get; set; }

    /// <summary>Filled by a dry run: what a real run would do.</summary>
    public List<string> Plan { get; } = [];

    public string Describe()
    {
        var parts = new List<string>();
        if (Copied > 0) parts.Add($"copied {Text.Count(Copied, "file")} ({Text.Bytes(CopiedBytes)})");
        if (Removed > 0) parts.Add($"removed {Text.Count(Removed, "file")}");
        if (Adopted > 0) parts.Add($"found {Text.Count(Adopted, "file")} already there");
        if (Failed > 0) parts.Add($"{Text.Count(Failed, "file")} failed");
        if (Unreadable > 0) parts.Add($"{Text.Count(Unreadable, "source or folder", "sources or folders")} could not be read");
        if (parts.Count == 0) parts.Add("nothing to copy");
        return $"{string.Join(", ", parts)}. {Text.Count(Scanned, "file")} in the backup, {Text.Duration(Elapsed)}.";
    }
}

public static class Text
{
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} bytes"
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    public static string Count(long n, string one, string? many = null)
        => $"{n.ToString("N0", CultureInfo.InvariantCulture)} {(n == 1 ? one : many ?? one + "s")}";

    public static string Duration(TimeSpan t)
        => t.TotalSeconds < 1 ? "under a second"
            : t.TotalSeconds < 90 ? string.Create(CultureInfo.InvariantCulture, $"{t.TotalSeconds:0} s")
            : t.TotalMinutes < 90 ? string.Create(CultureInfo.InvariantCulture, $"{t.TotalMinutes:0} min")
            : string.Create(CultureInfo.InvariantCulture, $"{t.TotalHours:0.#} h");

    public static string Ago(DateTime utc)
    {
        var t = DateTime.UtcNow - utc;
        return t.TotalSeconds < 60 ? "just now"
            : t.TotalMinutes < 90 ? string.Create(CultureInfo.InvariantCulture, $"{t.TotalMinutes:0} min ago")
            : t.TotalHours < 36 ? string.Create(CultureInfo.InvariantCulture, $"{t.TotalHours:0} h ago")
            : string.Create(CultureInfo.InvariantCulture, $"{t.TotalDays:0} days ago");
    }
}
