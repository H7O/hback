using System.Collections.Concurrent;
using Com.H.Threading.Scheduler;
using HBack.Config;
using HBack.Engine;

namespace HBack;

/// <summary>
/// The service: Com.H.Threading.Scheduler watches hback.xml and says when a backup is due, and this
/// class runs it. The scheduler re-reads the file when it changes, so editing the configuration needs
/// no restart, and an edited backup runs straight away because the scheduler sees it as a new task.
/// </summary>
public sealed class Worker(ILogger<Worker> log, ISchedulerService scheduler, BackupService backups, AppPaths paths) : BackgroundService
{
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("hback {Version} is following the schedule in {Config}. Its database and logs are in {Data}.",
            Cli.Version, paths.ConfigPath, paths.DataDir);

        scheduler.IsDue += OnDueAsync;
        scheduler.TaskLoadingError += (_, e, _) =>
        {
            log.LogError("Can't read the configuration: {Message}", e.Exception?.Message);
            return Task.CompletedTask;
        };

        await scheduler.StartAsync(stoppingToken);

        // The scheduler returns as soon as it is told to stop. A backup that was running is still
        // writing its last record, and the state database must stay open until it has.
        await Task.WhenAll(_running.Keys);
        log.LogInformation("hback stopped.");
    }

    private async Task OnDueAsync(object sender, HTaskEventArgs e, CancellationToken ct)
    {
        var work = RunAsync(e, ct);
        _running[work] = 0;
        try
        {
            await work;
        }
        finally
        {
            _running.TryRemove(work, out _);
        }
    }

    private async Task RunAsync(HTaskEventArgs e, CancellationToken ct)
    {
        // Nothing may escape from here. When a handler throws, the scheduler does not record the run,
        // finds the task still due a second later, and runs it again, without end. A backup that
        // can't reach its server would hammer it. So a failure is logged and recorded here, and
        // waiting and trying again is done by BackupService.
        try
        {
            BackupJob job;
            try
            {
                job = BackupJob.From(e.Task);
            }
            catch (ConfigException ex)
            {
                log.LogError("{Message} This backup did not run.", ex.Message);
                return;
            }

            if (BackupService.FindConflict(job, Others(e.Task)) is { } conflict)
            {
                log.LogError("{Conflict} '{Backup}' did not run.", conflict, job.Name);
                return;
            }

            await backups.RunAsync(job, retry: true, dryRun: false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // hback is stopping.
        }
        catch (Exception ex)
        {
            log.LogError(ex, "A backup stopped on an unexpected error: {Message}", ex.Message);
        }
    }

    /// <summary>The other backups in the configuration that can be read.</summary>
    private static List<BackupJob> Others(IHTaskItem task)
    {
        var others = new List<BackupJob>();
        foreach (var other in task.AllTasks)
        {
            if (other is null || ReferenceEquals(other, task)) continue;
            try
            {
                others.Add(BackupJob.From(other));
            }
            catch (ConfigException)
            {
                // It reports its own mistake when it is due.
            }
        }
        return others;
    }
}
