using System.Globalization;
using System.Text.RegularExpressions;
using Com.H.Threading.Scheduler;

namespace HBack.Config;

/// <summary>
/// The <c>&lt;sys&gt;</c> element of a backup, which says when it runs. The element belongs to
/// Com.H.Threading.Scheduler, and the scheduler is what acts on it. This class does two things the
/// scheduler doesn't: it refuses what the scheduler would pass over without a word, and it says a
/// schedule back in words.
/// <para>
/// The scheduler leaves out a value it can't read and ignores a tag it doesn't know. A time written
/// as "5am" is then no time at all, and the backup runs at midnight. A day written as "Mon" never
/// comes, and the backup never runs. Nobody is told either way. So hback reads the element first,
/// and a backup whose schedule can't mean what was written does not run until it is put right.
/// </para>
/// </summary>
public static partial class ScheduleRules
{
    /// <summary>The tags hback's page on scheduling describes, in the order it describes them.</summary>
    public const string Tags = "interval, time, until_time, dow, dom, eom, doy, dates, not_before, not_after, enabled and ignore_log_on_restart";

    /// <summary>
    /// Tags of the scheduler that are let through unread. They do no harm in a backup, and no good:
    /// hback tries again by its own settings, and a backup has nothing to repeat over.
    /// </summary>
    private static readonly string[] LetThrough = ["sleep_on_error", "retry_attempts_on_error", "repeat", "now"];

    [GeneratedRegex(@"^\d{1,2}:\d{2}(:\d{2})?$")]
    private static partial Regex TimeOfDay();

    [GeneratedRegex(@"^\d+(\.\.\d+)?(,\d+(\.\.\d+)?)*$")]
    private static partial Regex DayList();

    /// <summary>Throws a <see cref="ConfigException"/> for a schedule the scheduler would misread.</summary>
    public static void Check(IHTaskItem task, string backup)
    {
        if (task.GetItem("sys") is not { } sys) return;

        if (BackupJob.HasTextOfItsOwn(sys))
            throw new ConfigException($"Backup '{backup}': <sys> holds text that is in no tag. Its rules are tags inside it, such as <time>05:00</time>.");

        TimeSpan? from = null, until = null;
        foreach (var rule in sys.Children ?? [])
        {
            if (rule is null) continue;
            var tag = rule.Name.ToLowerInvariant();
            var value = Regex.Replace((rule.GetValue() ?? "").Trim(), @"\s+", " ");
            ConfigException Wrong(string how) => new($"Backup '{backup}': <{tag}> in <sys> is '{value}'. {how}");

            switch (tag)
            {
                case "interval":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds < 1)
                        throw Wrong("It is a whole number of milliseconds, from 1 to 2147483647. 1800000 is every 30 minutes.");
                    break;

                case "time" or "until_time":
                    if (!TimeOfDay().IsMatch(value) || !TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time) || time >= TimeSpan.FromDays(1))
                        throw Wrong("Write one time of day by the 24-hour clock, such as 05:00 or 16:30.");
                    if (tag == "time") from = time; else until = time;
                    break;

                case "dow":
                    var days = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (days.Length == 0 || days.Any(day => !Enum.GetNames<DayOfWeek>().Contains(day, StringComparer.OrdinalIgnoreCase)))
                        throw Wrong("Write the days of the week in full, with commas between them, such as Monday,Thursday.");
                    break;

                case "dom" or "doy":
                    var last = tag == "dom" ? 31 : 366;
                    var list = value.Replace(" ", "");
                    if (!DayList().IsMatch(list) || Regex.Matches(list, @"\d+").Any(n => !int.TryParse(n.Value, out var day) || day < 1 || day > last))
                        throw Wrong(tag == "dom"
                            ? "Write days of the month from 1 to 31, such as 1,15 or 25..28."
                            : "Write days of the year from 1 to 366, such as 1,182 or 100..120.");
                    break;

                case "dates":
                    // Read as the scheduler reads them, which leaves out a date it can't make sense of.
                    var dates = (rule.GetValue() ?? "").Split(['|', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (dates.Length == 0 || dates.Any(date => !DateTime.TryParse(date, out _)))
                        throw Wrong("Write each date as 2026-12-24, with | between two dates or one date to a line.");
                    break;

                case "not_before" or "not_after":
                    if (!DateTime.TryParse(value, out _))
                        throw Wrong("Write a date and a time, such as 2026-11-12 16:30.");
                    break;

                case "eom" or "enabled" or "ignore_log_on_restart":
                    if (!value.Equals("true", StringComparison.OrdinalIgnoreCase) && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
                        throw Wrong("It is true or false.");
                    break;

                case "bom":
                    throw new ConfigException($"Backup '{backup}': the scheduler does not act on <bom> in <sys>. For the first day of every month write <dom>1</dom>.");

                default:
                    if (!LetThrough.Contains(tag))
                        throw new ConfigException($"Backup '{backup}': <sys> holds a <{rule.Name}>, which is not one of its tags. They are {Tags}.");
                    break;
            }
        }

        // Each is checked against the same day's clock, so a stretch across midnight never comes.
        if (from is not null && until is not null && until <= from)
            throw new ConfigException($"Backup '{backup}': <until_time> {until:hh\\:mm} in <sys> is not later than <time> {from:hh\\:mm}. " +
                "The two mark out a part of one day, which can't run past midnight.");
    }

    /// <summary>"every 30 min", "once a day at 05:00, not before 2026-11-12 16:30": the scheduler's own reading of the entry, in words.</summary>
    public static string Describe(IHTaskControlProperties? schedule)
    {
        if (schedule is null) return "none (runs only with 'hback run')";

        var text = schedule.Interval is { } ms ? Every(ms) : "once a day";
        if (schedule.Time is { } time)
            text += schedule.Interval is null ? $" at {time:hh\\:mm}" : $", from {time:hh\\:mm}";
        if (schedule.UntilTime is { } until) text += $" until {until:hh\\:mm}";

        if (schedule.DaysOfWeek?.ToList() is { Count: > 0 } weekdays)
            text += " on " + string.Join(", ", weekdays.Select(day => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(day.ToLowerInvariant())));
        if (schedule.DaysOfMonth?.Distinct().ToList() is { Count: > 0 } ofMonth)
            text += $", on day{(ofMonth.Count > 1 ? "s" : "")} {Ranges(ofMonth)} of the month";
        if (schedule.LastDayOfMonth == true) text += ", on the last day of the month";
        if (schedule.DaysOfYear?.Distinct().ToList() is { Count: > 0 } ofYear)
            text += $", on day{(ofYear.Count > 1 ? "s" : "")} {Ranges(ofYear)} of the year";
        if (schedule.Dates?.Select(date => date.Date).Distinct().Order().ToList() is { Count: > 0 } dates)
            text += ", only on " + string.Join(", ", dates.Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        if (schedule.NotBefore is { } notBefore) text += $", not before {Moment(notBefore)}";
        if (schedule.NotAfter is { } notAfter) text += $", not after {Moment(notAfter)}";
        if (schedule.IgnoreLogOnRestart) text += ", and again each time hback starts";
        if (!schedule.Enabled) text += " (disabled)";
        return text;
    }

    private static string Moment(DateTime when) => when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// "every 30 minutes", "every hour": the interval in the largest unit that holds it exactly.
    /// It is never rounded, because this is what tells someone whether the number they wrote is the
    /// one they meant.
    /// </summary>
    private static string Every(int milliseconds)
    {
        var (count, unit) = milliseconds switch
        {
            _ when milliseconds % 3_600_000 == 0 => (milliseconds / 3_600_000, "hour"),
            _ when milliseconds % 60_000 == 0 => (milliseconds / 60_000, "minute"),
            _ when milliseconds % 1000 == 0 => (milliseconds / 1000, "second"),
            _ => (milliseconds, "millisecond"),
        };
        return count == 1 ? $"every {unit}" : $"every {count} {unit}s";
    }

    /// <summary>"1, 15, 25-28" for 1, 15, 25, 26, 27, 28.</summary>
    private static string Ranges(IEnumerable<int> numbers)
    {
        var parts = new List<string>();
        int first = 0, last = 0;
        var any = false;
        void Close() { if (any) parts.Add(first == last ? $"{first}" : $"{first}-{last}"); }
        foreach (var n in numbers.Order())
        {
            if (any && n == last + 1) { last = n; continue; }
            Close();
            (first, last, any) = (n, n, true);
        }
        Close();
        return string.Join(", ", parts);
    }
}
