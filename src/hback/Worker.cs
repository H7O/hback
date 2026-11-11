using System.Collections.Concurrent;
using Com.H.Threading.Scheduler;
using HBack.Config;
using HBack.Engine;

namespace HBack;

/// <summary>How the service ended, for the process's exit code.</summary>
public sealed class ServiceOutcome
{
    public bool Failed { get; set; }
}

/// <summary>
/// The service: Com.H.Threading.Scheduler watches hback.xml and says when a backup is due, and this
/// class runs it. The scheduler re-reads the file when it changes, so editing the configuration needs
/// no restart, and an edited backup runs straight away because the scheduler sees it as a new task.
/// </summary>
public sealed class Worker(ILogger<Worker> log, ISchedulerService scheduler, BackupService backups, AppPaths paths,
    ServiceOutcome outcome, IHostApplicationLifetime lifetime) : BackgroundService
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

        try
        {
            await scheduler.StartAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without the scheduler nothing would ever run, and a service that sits there doing
            // nothing looks healthy. It stops instead, and says so with a failing exit code, so
            // that whatever started it (systemd, the Windows service manager) knows.
            log.LogError(ex, "The scheduler could not run, so hback is stopping: {Message}", ex.Message);
            outcome.Failed = true;
            lifetime.StopApplication();
            return;
        }

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
        // Nothing may escape from here. When a handler throws, the scheduler takes the run as failed
        // and tries it again by its own rules: after the entry's sleep_on_error, or after its interval,
        // or every 60 seconds for a backup that runs once a day, and without end unless the entry
        // sets retry_attempts_on_error. That would come on top of hback's own retries and ignore the
        // backup's retries and retry_wait_minutes. So a failure is logged and recorded here, and
        // waiting and trying again is done by BackupService.
        // (Before Com.H.Threading.Scheduler 11.0.0 the scheduler tried again every second.)
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
