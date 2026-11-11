using System.Globalization;
using System.Reflection;
using Com.H.Threading.Scheduler;
using HBack.Config;
using HBack.Destinations;
using HBack.Engine;
using HBack.State;

namespace HBack;

/// <summary>The command line: <c>hback</c>, <c>hback run</c>, <c>hback status</c>, and so on.</summary>
public static class Cli
{
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    private const string Help = """
        hback copies the folders you pick to a local folder or an SFTP server, on a schedule.

        Usage:
          hback                     Follow the schedule in hback.xml until stopped. This is what
                                    a Windows service or a systemd unit runs.
          hback run [name ...]      Run the named backups now, or all of them, then exit.
              --dry-run             Only say what would be copied and removed.
          hback status [name]       How the last run of each backup went. With a name, its last 10 runs.
          hback verify [name ...]   Check that the files hback recorded are at the destination.
              --fix                 Forget the ones that are missing, so the next run copies them again.
          hback restore <name> --to <folder>
              --path <path>         Restore only this folder or file. Either its original path
                                    (C:\code\app) or its path in the backup (C/code/app).
              --at <history folder> Restore the copies set aside by one run (a folder name under
                                    history/, like 2026-11-11_103000) instead of the current files.
          hback help | version

        Options for every command:
          --config <path>           hback.xml, or a folder of .xml files. Default: beside the program.
                                    The HBACK_CONFIG environment variable sets it too.
          --data <folder>           Where hback keeps its database and logs. Default: the "data"
                                    folder beside the program. HBACK_DATA sets it too.
          --verbose                 Log more.
        """;

    private sealed class UsageException(string message) : Exception(message);

    private sealed class Options
    {
        public string Command = "service";
        public List<string> Names = [];
        public string? Config, Data, To, Path, At;
        public bool DryRun, Fix, Verbose;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            var words = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string Value() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{a} needs a value.");
                switch (a)
                {
                    case "--config": o.Config = Value(); break;
                    case "--data": o.Data = Value(); break;
                    case "--to": o.To = Value(); break;
                    case "--path": o.Path = Value(); break;
                    case "--at": o.At = Value(); break;
                    case "--dry-run": o.DryRun = true; break;
                    case "--fix": o.Fix = true; break;
                    case "--verbose" or "-v": o.Verbose = true; break;
                    case "--help" or "-h" or "-?" or "/?": words.Insert(0, "help"); break;
                    case "--version": words.Insert(0, "version"); break;
                    default:
                        if (a.StartsWith('-')) throw new UsageException($"Unknown option '{a}'.");
                        words.Add(a);
                        break;
                }
            }
            if (words.Count > 0)
            {
                o.Command = words[0].ToLowerInvariant();
                o.Names = words.Skip(1).ToList();
            }
            return o;
        }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run 'hback help' to see the commands.");
            return 2;
        }

        switch (options.Command)
        {
            case "help":
                Console.WriteLine(Help);
                return 0;
            case "version":
                Console.WriteLine($"hback {Version}");
                return 0;
            case "service" or "run" or "status" or "verify" or "restore":
                break;
            default:
                Console.Error.WriteLine($"Unknown command '{options.Command}'. Run 'hback help' to see the commands.");
                return 2;
        }

        var paths = AppPaths.Resolve(options.Config, options.Data);
        if (!paths.ConfigExists)
        {
            Console.Error.WriteLine($"There is no configuration at {paths.ConfigPath}.");
            Console.Error.WriteLine($"Copy {AppPaths.SampleFileName} to {AppPaths.ConfigFileName}, list your folders and a destination in it, and run hback again.");
            return 2;
        }

        try
        {
            if (options.Command == "service")
                return await RunServiceAsync(paths, options);

            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            using var loggers = Logging.Create(paths, options.Verbose);
            await using var state = await StateStore.OpenAsync(paths.DbPath);
            return options.Command switch
            {
                "run" => await RunBackupsAsync(paths, options, state, loggers, stop.Token),
                "status" => await ShowStatusAsync(paths, options, state),
                "verify" => await VerifyAsync(paths, options, state, loggers, stop.Token),
                _ => await RestoreAsync(paths, options, state, loggers, stop.Token),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Stopped.");
            return 1;
        }
        catch (Exception ex) when (ex is ConfigException or DestinationException or UsageException)
        {
            Console.Error.WriteLine(ex.Message);
            return ex is DestinationException ? 1 : 2;
        }
    }

    #region service

    private static async Task<int> RunServiceAsync(AppPaths paths, Options options)
    {
        await using var state = await StateStore.OpenAsync(paths.DbPath);
        using var host = BuildServiceHost(paths, state, logging => Logging.Configure(logging, paths, options.Verbose));
        await host.RunAsync();
        return 0;
    }

    /// <summary>The service, ready to start. The caller keeps the state database open until the host has stopped.</summary>
    internal static IHost BuildServiceHost(AppPaths paths, StateStore state, Action<ILoggingBuilder> logging)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
        logging(builder.Logging);

        // Each of these two does nothing unless hback was started by that service manager.
        builder.Services.AddWindowsService(o => o.ServiceName = "hback");
        builder.Services.AddSystemd();

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSchedulerService(o => o.ConfigPath = paths.ConfigPath);
        builder.Services.AddHostedService<Worker>();
        return builder.Build();
    }

    #endregion

    #region run

    private static async Task<int> RunBackupsAsync(AppPaths paths, Options options, StateStore state, ILoggerFactory loggers, CancellationToken ct)
    {
        var all = LoadJobs(paths);
        var jobs = Select(all, options.Names);
        var service = new BackupService(paths, state, loggers);

        var worst = 0;
        foreach (var job in jobs)
        {
            if (BackupService.FindConflict(job, all) is { } conflict)
                throw new ConfigException(conflict);

            var summary = await service.RunAsync(job, retry: false, options.DryRun, ct);
            if (summary.Status is not (RunStatus.Ok or RunStatus.Skipped)) worst = 1;
            if (ct.IsCancellationRequested) break;
        }
        return worst;
    }

    #endregion

    #region status

    private static async Task<int> ShowStatusAsync(AppPaths paths, Options options, StateStore state)
    {
        var entries = ConfigLoader.Load(paths.ConfigPath);
        foreach (var error in entries.Where(e => e.Error is not null))
            Console.WriteLine($"Configuration problem: {error.Error}");

        var jobs = entries.Where(e => e.Job is not null).Select(e => e.Job!).ToList();
        if (options.Names.Count > 0) jobs = Select(jobs, options.Names);
        if (jobs.Count == 0)
        {
            Console.WriteLine($"No backups are defined in {paths.ConfigPath}.");
            return 0;
        }

        var detailed = options.Names.Count > 0;
        foreach (var job in jobs)
        {
            Console.WriteLine();
            Console.WriteLine(job.Name);
            Console.WriteLine($"  to         {job.Destination.Display}");
            Console.WriteLine($"  schedule   {job.ScheduleText}");

            var runs = await state.GetLastRunsAsync(job.Name, detailed ? 10 : 1);
            if (runs.Count == 0)
            {
                Console.WriteLine("  last run   never");
                continue;
            }

            var busy = IsRunning(paths, job);
            Console.WriteLine($"  last run   {Describe(runs[0], busy)}");
            if (runs[0].Status != "ok")
                Console.WriteLine($"  last good  {(await state.GetLastGoodRunAsync(job.Name) is { } good ? When(good) : "never")}");
            if (await state.FindDestinationIdAsync(job.Destination.Id) is { } dest)
            {
                var (files, bytes) = await state.CountFilesAsync(dest);
                Console.WriteLine($"  holds      {Text.Count(files, "file")}, {Text.Bytes(bytes)}");
            }
            if (detailed)
            {
                Console.WriteLine("  earlier runs");
                foreach (var run in runs.Skip(1))
                    Console.WriteLine($"    {Describe(run, false)}");
            }
        }
        Console.WriteLine();
        return 0;
    }

    private static bool IsRunning(AppPaths paths, BackupJob job)
    {
        using var probe = RunLock.TryAcquire(paths.LockDir, job.Destination.Id);
        return probe is null;
    }

    private static string Describe(RunRecord run, bool busy)
    {
        var status = run.Status switch
        {
            "running" => busy ? "running now" : "cut off (hback was stopped or the machine went down)",
            "ok" => "ok",
            "errors" => "finished with problems",
            _ => run.Status,
        };
        var numbers = new List<string>();
        if (run.Copied > 0) numbers.Add($"copied {Text.Count(run.Copied, "file")} ({Text.Bytes(run.CopiedBytes)})");
        if (run.Removed > 0) numbers.Add($"removed {run.Removed.ToString("N0", CultureInfo.InvariantCulture)}");
        if (run.Failed > 0) numbers.Add($"{run.Failed.ToString("N0", CultureInfo.InvariantCulture)} failed");
        if (numbers.Count == 0 && run.Status == "ok") numbers.Add("nothing to copy");
        var text = $"{When(run.StartedUtc)}: {status}";
        if (numbers.Count > 0) text += ", " + string.Join(", ", numbers);
        if (run.Status != "ok" && !string.IsNullOrWhiteSpace(run.Message)) text += ". " + run.Message;
        return text;
    }

    private static string When(DateTime utc)
        => $"{utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} ({Text.Ago(utc)})";

    #endregion

    #region verify

    private static async Task<int> VerifyAsync(AppPaths paths, Options options, StateStore state, ILoggerFactory loggers, CancellationToken ct)
    {
        var verifier = new Verifier(state, loggers.CreateLogger("hback"));
        var worst = 0;
        foreach (var job in Select(LoadJobs(paths), options.Names))
        {
            var result = await verifier.VerifyAsync(job, options.Fix, ct);
            if (result.NeverRan)
            {
                Console.WriteLine($"{job.Name}: has not run yet, so there is nothing to check.");
                continue;
            }
            if (result.Ok)
            {
                Console.WriteLine($"{job.Name}: all {Text.Count(result.Checked, "file")} are at {job.Destination.Display}.");
                continue;
            }

            worst = 1;
            Console.WriteLine($"{job.Name}: {Text.Count(result.Missing.Count, "file")} missing and {result.WrongSize.Count} with the wrong size, out of {result.Checked.ToString("N0", CultureInfo.InvariantCulture)}.");
            foreach (var path in result.Missing.Take(20)) Console.WriteLine($"  missing     {path}");
            foreach (var path in result.WrongSize.Take(20)) Console.WriteLine($"  wrong size  {path}");
            if (result.Missing.Count + result.WrongSize.Count > 40) Console.WriteLine("  ...");
            Console.WriteLine(options.Fix
                ? "  hback forgot these files. The next run copies them again."
                : "  Run 'hback verify --fix' to have the next run copy them again.");
        }
        return worst;
    }

    #endregion

    #region restore

    private static async Task<int> RestoreAsync(AppPaths paths, Options options, StateStore state, ILoggerFactory loggers, CancellationToken ct)
    {
        if (options.Names.Count != 1) throw new UsageException("Name one backup to restore from: hback restore <name> --to <folder>");
        if (string.IsNullOrWhiteSpace(options.To)) throw new UsageException("Say where to put the files: hback restore <name> --to <folder>");

        var job = Select(LoadJobs(paths), options.Names)[0];
        var result = await new Restorer(state, loggers.CreateLogger("hback")).RestoreAsync(job, options.Path, options.At, options.To, ct);

        Console.WriteLine($"Restored {Text.Count(result.Restored, "file")} ({Text.Bytes(result.Bytes)}) to {Path.GetFullPath(options.To)}."
            + (result.AlreadyThere > 0 ? $" {Text.Count(result.AlreadyThere, "file")} were there already." : "")
            + (result.Failed > 0 ? $" {Text.Count(result.Failed, "file")} failed." : ""));
        return result.Failed > 0 ? 1 : 0;
    }

    #endregion

    /// <summary>The backups in the configuration. Any mistake in it stops the command.</summary>
    private static List<BackupJob> LoadJobs(AppPaths paths)
    {
        var entries = ConfigLoader.Load(paths.ConfigPath);
        if (entries.FirstOrDefault(e => e.Error is not null) is { } bad)
            throw new ConfigException(bad.Error!);
        var jobs = entries.Select(e => e.Job!).ToList();
        if (jobs.Count == 0)
            throw new ConfigException($"No backups are defined in {paths.ConfigPath}.");
        return jobs;
    }

    private static List<BackupJob> Select(List<BackupJob> jobs, List<string> names)
    {
        if (names.Count == 0) return jobs;
        var selected = new List<BackupJob>();
        foreach (var name in names)
            selected.Add(jobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new UsageException($"There is no backup named '{name}'. The backups are: {string.Join(", ", jobs.Select(j => j.Name))}."));
        return selected;
    }
}
