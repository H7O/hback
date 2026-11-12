using HBack.Config;
using HBack.State;

namespace HBack.Engine;

/// <summary>
/// What the service and the <c>run</c> command both do with a backup that is due: run it to each of
/// its destinations, say how each went, and try again later the ones that were out of reach.
/// </summary>
public sealed class BackupService(AppPaths paths, StateStore state, ILoggerFactory loggers)
{
    private readonly ILogger _log = loggers.CreateLogger("hback");
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _idle = new(StringComparer.OrdinalIgnoreCase);

    public BackupRunner Runner { get; } = new(paths, state, loggers.CreateLogger("hback"));

    /// <param name="retry">When true, a run that could not reach its destination is repeated after
    /// a wait, as many times as the backup's <c>retries</c> setting says. Only the destinations that
    /// failed are tried again: the others have their files.</param>
    /// <param name="only">The destinations to serve. Null serves them all.</param>
    /// <returns>How the last run to each destination went, in the order the backup lists them.</returns>
    public async Task<List<RunSummary>> RunAsync(BackupJob job, bool retry, bool dryRun, CancellationToken ct,
        IReadOnlyCollection<DestinationSpec>? only = null)
    {
        var latest = new Dictionary<DestinationSpec, RunSummary>();
        for (var attempt = 0; ; attempt++)
        {
            var summaries = await Runner.RunAsync(job, dryRun, ct, only, summary => Report(job, summary, dryRun));
            foreach (var summary in summaries)
                latest[summary.Destination] = summary;

            var failed = summaries.Where(s => s.Status == RunStatus.Failed && s.WorthRetrying).Select(s => s.Destination).ToList();
            if (failed.Count == 0 || !retry || attempt >= job.Retries || ct.IsCancellationRequested)
                break;

            foreach (var destination in failed)
                _log.LogWarning("{Backup}: trying again in {Minutes} min (attempt {Attempt} of {Retries}).",
                    job.LabelFor(destination), job.RetryWaitMinutes, attempt + 1, job.Retries);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(job.RetryWaitMinutes), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            only = failed;
        }
        return job.Destinations.Where(latest.ContainsKey).Select(d => latest[d]).ToList();
    }

    private void Report(BackupJob job, RunSummary summary, bool dryRun)
    {
        var label = job.LabelFor(summary.Destination);
        if (dryRun && summary.Status is RunStatus.Ok or RunStatus.Errors && summary.Plan.Count > 0)
        {
            _log.LogInformation("{Backup}: dry run, nothing was changed. {Plan}", label, string.Join(Environment.NewLine, summary.Plan));
            return;
        }

        // A backup on a short interval mostly finds nothing to do. That is said once, and not again
        // until something has happened in between. "hback status" always shows the latest run.
        var idle = summary.Status == RunStatus.Ok && summary.Copied == 0 && summary.Removed == 0 && summary.Adopted == 0;
        var saidBefore = idle && _idle.TryGetValue(label, out var was) && was;
        _idle[label] = idle;

        switch (summary.Status)
        {
            case RunStatus.Ok:
                _log.Log(saidBefore ? LogLevel.Debug : LogLevel.Information, "{Backup}: {Summary}", label, summary.Describe());
                break;
            case RunStatus.Errors:
                _log.LogWarning("{Backup}: finished with problems. {Summary}", label, summary.Describe());
                break;
            case RunStatus.Failed:
                _log.LogError("{Backup}: failed. {Message}", label, summary.Message);
                break;
            case RunStatus.Cancelled:
                _log.LogWarning("{Backup}: {Message}", label, summary.Message);
                break;
            case RunStatus.Skipped:
                _log.LogInformation("{Backup}: {Message}", label, summary.Message);
                break;
        }
    }

    /// <summary>
    /// Why <paramref name="job"/> must not run beside <paramref name="others"/>, or null when it may.
    /// Two backups writing into the same folders of one destination would each take the other's files
    /// for deleted ones.
    /// </summary>
    public static string? FindConflict(BackupJob job, IEnumerable<BackupJob> others)
    {
        foreach (var other in others)
        {
            if (ReferenceEquals(other, job)) continue;
            if (string.Equals(other.Name, job.Name, StringComparison.OrdinalIgnoreCase))
                return $"Two backups are named '{job.Name}'. Give each backup its own <name>.";
            if (job.Destinations.FirstOrDefault(d => other.Destinations.Any(d.SharesFoldersWith)) is { } shared)
                return $"The backups '{job.Name}' and '{other.Name}' have the same destination ({shared.Display}). " +
                       "Give each backup its own folder, or each would take the other's files for deleted ones.";
        }
        return null;
    }
}
