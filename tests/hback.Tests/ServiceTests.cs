using HBack.Engine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace HBack.Tests;

/// <summary>
/// The service as it really runs: the host, Com.H.Threading.Scheduler reading hback.xml, and the
/// worker running what the scheduler says is due.
/// </summary>
public class ServiceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private TestEnv _env = null!;
    private string _destination = "";

    public async Task InitializeAsync()
    {
        _env = await TestEnv.CreateAsync(output);
        _destination = Path.Combine(_env.Root, "dest");
        Directory.CreateDirectory(_destination);
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    private void WriteConfig(string schedule, string extra = "")
        => File.WriteAllText(_env.Paths.ConfigPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <hback>
              <backup>
                <name>svc</name>
                {schedule}
                <sources>
                  {_env.SourceDir}
                </sources>
                <exclude>
                  *.tmp
                </exclude>
                <destination>{_destination}</destination>
                {extra}
              </backup>
            </hback>
            """);

    private IHost Build() => Cli.BuildServiceHost(_env.Paths, _env.State, logging =>
    {
        logging.ClearProviders();
        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddProvider(new TestLoggerProvider(_env.Log));
    });

    private string Current(string relative)
        => Path.Combine(_destination, BackupLayout.Current, _env.InBackup(relative).Replace('/', Path.DirectorySeparatorChar));

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    private Task WaitForFileAsync(string relative, string content)
        => WaitForAsync(() => Task.FromResult(File.Exists(Current(relative)) && TryRead(Current(relative)) == content), $"{relative} to be backed up as '{content}'");

    private static string? TryRead(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    [Fact]
    public async Task The_service_backs_up_on_its_interval_and_picks_up_later_changes()
    {
        _env.Write("a.txt", "alpha");
        _env.Write("skip.tmp", "temp");
        WriteConfig("<sys><interval>1000</interval></sys>");

        using var host = Build();
        await host.StartAsync();
        try
        {
            // A backup that has never run is due at once.
            await WaitForFileAsync("a.txt", "alpha");
            Assert.False(File.Exists(Current("skip.tmp")));

            // A change made while the service runs is picked up by a later interval.
            _env.Write("b.txt", "beta");
            _env.Write("a.txt", "alpha, edited");
            await WaitForFileAsync("b.txt", "beta");
            await WaitForFileAsync("a.txt", "alpha, edited");
        }
        finally
        {
            await host.StopAsync();
        }

        var runs = await _env.State.GetLastRunsAsync("svc", 50);
        Assert.True(runs.Count >= 2, "The interval should have fired more than once.");
        Assert.All(runs, r => Assert.Equal("ok", r.Status));
        Assert.DoesNotContain(_env.Log.Lines, l => l.StartsWith("[Error]"));
    }

    [Fact]
    public async Task The_service_sends_the_files_straight_into_every_destination_of_a_backup()
    {
        _env.Write("app/a.txt", "alpha");
        var second = Path.Combine(_env.Root, "dest2");
        Directory.CreateDirectory(second);
        // Laid out for handing files on to other machines: no "current" folder and none of the source's path.
        File.WriteAllText(_env.Paths.ConfigPath, $"""
            <hback>
              <backup>
                <name>svc</name>
                <sys><interval>1000</interval></sys>
                <sources>{_env.SourceDir}</sources>
                <source_path>none</source_path>
                <current_folder>.</current_folder>
                <destinations>
                  {_destination}
                  {second}
                </destinations>
              </backup>
            </hback>
            """);
        string[] places = [_destination, second];

        using var host = Build();
        await host.StartAsync();
        try
        {
            foreach (var place in places)
                await WaitForAsync(() => Task.FromResult(TryRead(Path.Combine(place, "app", "a.txt")) == "alpha"), $"a.txt to arrive in {place}");

            _env.Write("app/a.txt", "alpha, edited");
            foreach (var place in places)
                await WaitForAsync(() => Task.FromResult(TryRead(Path.Combine(place, "app", "a.txt")) == "alpha, edited"), $"the edit to arrive in {place}");
        }
        finally
        {
            await host.StopAsync();
        }

        foreach (var place in places)
        {
            Assert.False(Directory.Exists(Path.Combine(place, BackupLayout.Current)));
            // The copy that the edit replaced is kept, out of the way of the files.
            var kept = Assert.Single(Directory.GetFiles(Path.Combine(place, ".hback", "history"), "a.txt", SearchOption.AllDirectories));
            Assert.Equal("alpha", File.ReadAllText(kept));
        }
        Assert.DoesNotContain(_env.Log.Lines, l => l.StartsWith("[Error]") || l.StartsWith("[Warning]"));
    }

    [Fact]
    public async Task Editing_the_configuration_takes_effect_without_a_restart()
    {
        _env.Write("a.txt", "alpha");
        _env.Write("notes.md", "notes");
        // A day-long interval: after the first run nothing more would happen today.
        WriteConfig("<sys><interval>86400000</interval></sys>");

        using var host = Build();
        await host.StartAsync();
        try
        {
            await WaitForFileAsync("notes.md", "notes");

            // Excluding *.md is a change to the backup, so the scheduler runs it again straight away,
            // and the file that is now excluded leaves current/.
            await Task.Delay(1100); // The scheduler notices a change by the file's time, to the second.
            File.WriteAllText(_env.Paths.ConfigPath, File.ReadAllText(_env.Paths.ConfigPath).Replace("*.tmp", "*.md"));
            await WaitForAsync(() => Task.FromResult(!File.Exists(Current("notes.md"))), "notes.md to leave the backup");
            Assert.True(File.Exists(Current("a.txt")));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_backup_that_fails_is_not_run_again_every_second()
    {
        _env.Write("a.txt", "alpha");
        Directory.Delete(_destination); // The destination is gone: every run fails.
        // sleep_on_error is how long the scheduler waits before it tries a task again that failed in
        // its eyes, which is a task whose handler threw. One second makes such a retry show up here.
        // (Before Com.H.Threading.Scheduler 11.0.0 it needed no setting: the retry came on every tick.)
        WriteConfig("<sys><interval>60000</interval><sleep_on_error>1000</sleep_on_error></sys>", "<retries>0</retries>");

        using var host = Build();
        await host.StartAsync();
        try
        {
            await WaitForAsync(async () => (await _env.State.GetLastRunsAsync("svc", 5)).Any(r => r.Status == "failed"), "the first run to fail");
            // Had the failure escaped to the scheduler, it would run the backup again every second and
            // several more runs would pile up here.
            await Task.Delay(3500);
        }
        finally
        {
            await host.StopAsync();
        }

        var runs = await _env.State.GetLastRunsAsync("svc", 50);
        Assert.Single(runs);
        Assert.Contains("doesn't exist", runs[0].Message);
    }

    [SkippableFact]
    public async Task A_daily_backup_runs_once_its_time_has_passed_and_not_before()
    {
        // One backup is set for five minutes ago and one for five minutes from now. Both times have to
        // be today's, and in the five minutes on each side of midnight one of them is not. The window
        // is kept that small on purpose: a skipped test does not fail a build, so a release built
        // inside it goes out without this test.
        var now = DateTime.Now;
        var passed = now.AddMinutes(-5);
        var ahead = now.AddMinutes(5);
        Skip.If(passed.Date != now.Date || ahead.Date != now.Date,
            "Too close to midnight to have a time earlier and a time later today.");
        _env.Write("a.txt", "alpha");
        var later = Path.Combine(_env.Root, "dest-later");
        Directory.CreateDirectory(later);
        // The first stands for a backup set for a time when the machine was off: it is due the moment hback runs.
        File.WriteAllText(_env.Paths.ConfigPath, $"""
            <hback>
              <backup>
                <name>svc</name>
                <sys><time>{passed:HH:mm}</time></sys>
                <sources>{_env.SourceDir}</sources>
                <destination>{_destination}</destination>
              </backup>
              <backup>
                <name>later</name>
                <sys><time>{ahead:HH:mm}</time></sys>
                <sources>{_env.SourceDir}</sources>
                <destination>{later}</destination>
              </backup>
            </hback>
            """);

        using var host = Build();
        await host.StartAsync();
        try
        {
            await WaitForFileAsync("a.txt", "alpha");
            // Once a day means once: the scheduler's ticks that follow must not run it again.
            await Task.Delay(2500);
        }
        finally
        {
            await host.StopAsync();
        }

        Assert.Single(await _env.State.GetLastRunsAsync("svc", 10));
        Assert.Empty(await _env.State.GetLastRunsAsync("later", 10));
        Assert.Empty(Directory.GetFileSystemEntries(later));
    }

    /// <summary>
    /// "Every day at 05:00, and the first time today at 16:30", as the README writes it: a daily
    /// time that has passed, and a moment before which the backup may not run. Nothing in hback
    /// was made for this. It follows from two rules of the scheduler, and this holds them to it.
    /// </summary>
    [Fact]
    public async Task A_daily_backup_whose_time_has_passed_waits_for_the_moment_it_may_not_run_before()
    {
        _env.Write("a.txt", "alpha");
        // Written to the second, so the moment is the start of that second.
        var soon = DateTime.Now.AddSeconds(6);
        var notBefore = new DateTime(soon.Year, soon.Month, soon.Day, soon.Hour, soon.Minute, soon.Second, DateTimeKind.Local);
        WriteConfig($"<sys><time>00:00</time><not_before>{notBefore:yyyy-MM-dd HH:mm:ss}</not_before></sys>");

        using var host = Build();
        await host.StartAsync();
        try
        {
            await WaitForFileAsync("a.txt", "alpha");
        }
        finally
        {
            await host.StopAsync();
        }

        // The scheduler looks every second. Left to its daily time alone, which passed at midnight,
        // the backup would have started as soon as hback did. It ran once, and not before the moment.
        var run = Assert.Single(await _env.State.GetLastRunsAsync("svc", 10));
        Assert.True(run.StartedUtc.AddSeconds(1) > notBefore.ToUniversalTime(),
            $"The backup started at {run.StartedUtc:HH:mm:ss.fff} UTC, before {notBefore.ToUniversalTime():HH:mm:ss} UTC.");
    }

    [Fact]
    public async Task The_status_command_fails_on_a_schedule_it_cannot_read_and_passes_once_it_is_put_right()
    {
        string[] status = ["status", "--config", _env.Paths.ConfigPath, "--data", Path.Combine(_env.Root, "status-data")];

        // Whoever has just written the configuration sees the mistake from the exit code alone.
        WriteConfig("<sys><time>5am</time></sys>");
        Assert.Equal(2, await Cli.RunAsync(status));

        WriteConfig("<sys><time>05:00</time></sys>");
        Assert.Equal(0, await Cli.RunAsync(status));
    }

    [Fact]
    public async Task A_configuration_that_is_not_valid_XML_stops_the_service_from_starting()
    {
        // The <backup> element is never closed.
        File.WriteAllText(_env.Paths.ConfigPath, "<hback>\n  <backup>\n    <name>svc</name>\n  <backup>\n</hback>");

        var exitCode = await Cli.RunAsync(["--config", _env.Paths.ConfigPath, "--data", Path.Combine(_env.Root, "service-data")])
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(2, exitCode);
        var problem = Assert.Single(HBack.Config.ConfigLoader.Load(_env.Paths.ConfigPath));
        Assert.True(problem.WholeFile);
        // The message says what is wrong and where, in the XML parser's words.
        Assert.Contains("'backup' start tag on line 4", problem.Error);
    }

    [Fact]
    public async Task A_configuration_that_breaks_while_the_service_runs_is_logged_and_the_backups_carry_on()
    {
        _env.Write("a.txt", "alpha");
        WriteConfig("<sys><interval>1000</interval></sys>");

        using var host = Build();
        await host.StartAsync();
        try
        {
            await WaitForFileAsync("a.txt", "alpha");

            // Saved half way through an edit.
            await Task.Delay(1100);
            var good = File.ReadAllText(_env.Paths.ConfigPath);
            File.WriteAllText(_env.Paths.ConfigPath, good.Replace("</backup>", ""));
            await WaitForAsync(() => Task.FromResult(_env.Log.Lines.Any(l => l.Contains("Can't read the configuration"))), "the broken file to be reported");

            // The backup it knew keeps running on its interval.
            _env.Write("b.txt", "beta");
            await WaitForFileAsync("b.txt", "beta");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task A_mistake_in_the_configuration_is_logged_and_the_service_keeps_running()
    {
        _env.Write("a.txt", "alpha");
        File.WriteAllText(_env.Paths.ConfigPath, $"""
            <hback>
              <backup>
                <name>broken</name>
                <sys><interval>60000</interval></sys>
                <destination>{_destination}</destination>
              </backup>
              <backup>
                <name>svc</name>
                <sys><interval>60000</interval></sys>
                <sources>{_env.SourceDir}</sources>
                <destination>{_destination}</destination>
              </backup>
            </hback>
            """);

        using var host = Build();
        await host.StartAsync();
        try
        {
            await WaitForFileAsync("a.txt", "alpha");
            await WaitForAsync(() => Task.FromResult(_env.Log.Lines.Any(l => l.Contains("no <sources>"))), "the mistake to be logged");
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
