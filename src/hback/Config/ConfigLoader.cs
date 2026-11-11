using Com.H.Threading.Scheduler;

namespace HBack.Config;

/// <summary>One entry of the configuration: the backup it describes, or why it could not be read.</summary>
/// <param name="WholeFile">True when the error is a file that is not valid XML, so none of its backups could be read.</param>
public sealed record ConfigEntry(BackupJob? Job, string? Error, bool WholeFile = false);

/// <summary>
/// Reads the backups out of hback.xml for the commands that don't go through the scheduler
/// (<c>run</c>, <c>status</c>, <c>verify</c>, <c>restore</c>). It uses the scheduler's own reader, so a
/// value means the same thing here as it does when the scheduler hands the entry over.
/// </summary>
public static class ConfigLoader
{
    public static List<ConfigEntry> Load(string configPath)
    {
        var loadErrors = new List<string>();
        var tasks = new XmlFileHTaskCollection(configPath, null);
        tasks.Error += (_, e, _) =>
        {
            loadErrors.Add(e.Exception?.Message ?? "The configuration could not be read.");
            return Task.CompletedTask;
        };

        var entries = new List<ConfigEntry>();
        foreach (var task in tasks)
        {
            if (task is null) continue;
            try
            {
                entries.Add(new ConfigEntry(BackupJob.From(task), null));
            }
            catch (ConfigException ex)
            {
                entries.Add(new ConfigEntry(null, ex.Message));
            }
        }
        entries.AddRange(loadErrors.Select(e => new ConfigEntry(null, e, WholeFile: true)));

        var duplicate = entries.Where(e => e.Job is not null)
            .GroupBy(e => e.Job!.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            entries.Add(new ConfigEntry(null, $"Two backups are named '{duplicate.Key}'. Give each backup its own <name>."));

        return entries;
    }
}
