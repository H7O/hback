using HBack.Config;
using HBack.State;

namespace HBack.Engine;

/// <summary>
/// What the service and the <c>run</c> command both do with a backup that is due: check that it can't
/// collide with another backup, run it, say how it went, and try again later if the destination was
/// out of reach.
/// </summary>
public sealed class BackupService(AppPaths paths, StateStore state, ILoggerFactory loggers)
{
    private readonly ILogger _log = loggers.CreateLogger("hback");
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _idle = new(StringComparer.OrdinalIgnoreCase);

    public BackupRunner Runner { get; } = new(paths, state, loggers.CreateLogger("hback"));

    /// <param name="retry">When true, a run that could not reach its destination is repeated after
    /// a wait, as many times as the backup's <c>retries</c> setting says.</param>
    public async Task<RunSummary> RunAsync(BackupJob job, bool retry, bool dryRun, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var summary = await Runner.RunAsync(job, dryRun, ct);
            Report(job, summary, dryRun);

            if (summary.Status != RunStatus.Failed || !retry || attempt >= job.Retries)
                return summary;

            _log.LogWarning("{Backup}: trying again in {Minutes} min (attempt {Attempt} of {Retries}).",
                job.Name, job.RetryWaitMinutes, attempt + 1, job.Retries);
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(job.RetryWaitMinutes), ct);
            }
            catch (OperationCanceledException)
            {
                return summary;
            }
        }
    }

    private void Report(BackupJob job, RunSummary summary, bool dryRun)
    {
        if (dryRun && summary.Status is RunStatus.Ok or RunStatus.Errors && summary.Plan.Count > 0)
        {
            _log.LogInformation("{Backup}: dry run, nothing was changed. {Plan}", job.Name, string.Join(Environment.NewLine, summary.Plan));
            return;
        }

        // A backup on a short interval mostly finds nothing to do. That is said once, and not again
        // until something has happened in between. "hback status" always shows the latest run.
        var idle = summary.Status == RunStatus.Ok && summary.Copied == 0 && summary.Removed == 0 && summary.Adopted == 0;
        var saidBefore = idle && _idle.TryGetValue(job.Name, out var was) && was;
        _idle[job.Name] = idle;

        switch (summary.Status)
        {
            case RunStatus.Ok:
                _log.Log(saidBefore ? LogLevel.Debug : LogLevel.Information, "{Backup}: {Summary}", job.Name, summary.Describe());
                break;
            case RunStatus.Errors:
                _log.LogWarning("{Backup}: finished with problems. {Summary}", job.Name, summary.Describe());
                break;
            case RunStatus.Failed:
                _log.LogError("{Backup}: failed. {Message}", job.Name, summary.Message);
                break;
            case RunStatus.Cancelled:
                _log.LogWarning("{Backup}: {Message}", job.Name, summary.Message);
                break;
            case RunStatus.Skipped:
                _log.LogInformation("{Backup}: {Message}", job.Name, summary.Message);
                break;
        }
    }

    /// <summary>
    /// Why <paramref name="job"/> must not run beside <paramref name="others"/>, or null when it may.
    /// Two backups writing to one destination folder would each take the other's files for deleted ones.
    /// </summary>
    public static string? FindConflict(BackupJob job, IEnumerable<BackupJob> others)
    {
        foreach (var other in others)
        {
            if (ReferenceEquals(other, job)) continue;
            if (string.Equals(other.Name, job.Name, StringComparison.OrdinalIgnoreCase))
                return $"Two backups are named '{job.Name}'. Give each backup its own <name>.";
            if (job.Destination.IsSftp == other.Destination.IsSftp
                && string.Equals(other.Destination.Id, job.Destination.Id,
                    job.Destination.IsSftp ? StringComparison.Ordinal : BackupJob.PathComparison))
                return $"The backups '{job.Name}' and '{other.Name}' have the same destination ({job.Destination.Display}). " +
                       "Give each backup its own folder, or each would take the other's files for deleted ones.";
        }
        return null;
    }
}
