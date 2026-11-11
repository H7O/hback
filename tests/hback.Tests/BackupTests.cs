using HBack.Config;
using HBack.Destinations;
using HBack.Engine;
using HBack.Scan;
using Xunit.Abstractions;

namespace HBack.Tests;

/// <summary>
/// What a backup must do, whatever it is stored on. The two classes at the end of this file run
/// every test here twice: against a folder, and against a real SFTP server.
/// </summary>
public abstract class BackupTests(ITestOutputHelper output) : IAsyncLifetime
{
    protected TestEnv Env { get; private set; } = null!;

    public async Task InitializeAsync() => Env = await TestEnv.CreateAsync(output);
    public async Task DisposeAsync() => await Env.DisposeAsync();

    /// <summary>Why this kind of destination can't be tested here, or null when it can.</summary>
    protected abstract string? Unavailable { get; }

    /// <summary>A destination of its own for the test.</summary>
    protected abstract DestinationSpec NewDestination();

    /// <summary>The folder on this machine where that destination keeps its files.</summary>
    protected abstract string DiskFolder(DestinationSpec destination);

    protected (BackupJob Job, string Disk) Setup(int keepHistoryDays = 30, params string[] excludes)
    {
        Skip.If(Unavailable is not null, Unavailable);
        var destination = NewDestination();
        return (Env.Job(destination, keepHistoryDays, excludes), DiskFolder(destination));
    }

    protected Task<RunSummary> RunAsync(BackupJob job) => Env.Runner().RunAsync(job, dryRun: false, CancellationToken.None);

    protected Troublemaker Trouble(BackupJob job) => new(DestinationFactory.For(job.Destination, Env.State, Env.Log));

    /// <summary>The path on this machine of a source file's copy in <c>current/</c>.</summary>
    protected string Current(string disk, string relative)
        => Path.Combine(disk, BackupLayout.Current, Env.InBackup(relative).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The files under a folder, as sorted paths with forward slashes.</summary>
    protected static List<string> FilesUnder(string folder)
        => !Directory.Exists(folder)
            ? []
            : Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToList();

    protected List<string> CurrentFiles(string disk) => FilesUnder(Path.Combine(disk, BackupLayout.Current));
    protected List<string> HistoryFiles(string disk) => FilesUnder(Path.Combine(disk, BackupLayout.History));

    protected List<string> InBackup(params string[] relatives)
        => relatives.Select(Env.InBackup).Order(StringComparer.Ordinal).ToList();

    [SkippableFact]
    public async Task The_first_run_copies_everything_except_what_is_excluded()
    {
        Env.Write("a.txt", "alpha");
        Env.Write("sub/deep/b.cs", "beta");
        Env.Write(".env", "secret");
        Env.Write("node_modules/x/junk.js", "junk");
        Env.Write("sub/bin/app.dll", "built");
        Env.Write("out.tmp", "temp");
        var (job, disk) = Setup(30, "node_modules/", "bin/", "*.tmp");

        var summary = await RunAsync(job);

        Assert.Equal(RunStatus.Ok, summary.Status);
        Assert.Equal(3, summary.Copied);
        Assert.Equal(InBackup(".env", "a.txt", "sub/deep/b.cs"), CurrentFiles(disk));
        Assert.Equal("alpha", File.ReadAllText(Current(disk, "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Current(disk, "sub/deep/b.cs")));
        Assert.Empty(FilesUnder(Path.Combine(disk, BackupLayout.Partial)));
    }

    [SkippableFact]
    public async Task A_copy_keeps_the_modified_time_of_its_source()
    {
        var source = Env.Write("a.txt", "alpha");
        var then = new DateTime(2024, 3, 9, 14, 30, 12, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, then);
        var (job, disk) = Setup();

        await RunAsync(job);

        Assert.InRange((File.GetLastWriteTimeUtc(Current(disk, "a.txt")) - then).TotalSeconds, -1, 1);
    }

    [SkippableFact]
    public async Task A_run_with_nothing_to_do_does_not_contact_the_destination()
    {
        Env.Write("a.txt", "alpha");
        var (job, _) = Setup();
        await RunAsync(job);

        var trouble = Trouble(job);
        trouble.OutOfReach = true;
        var summary = await Env.Runner(_ => trouble).RunAsync(job, false, CancellationToken.None);

        Assert.Equal(RunStatus.Ok, summary.Status);
        Assert.Equal(0, summary.Copied);
        Assert.Equal(0, trouble.Connections);
    }

    [SkippableFact]
    public async Task Runs_with_nothing_to_do_do_not_pile_up_in_the_history_of_runs()
    {
        Env.Write("a.txt", "alpha");
        var (job, _) = Setup();
        await RunAsync(job);
        for (var i = 0; i < 4; i++) await RunAsync(job);

        var runs = await Env.State.GetLastRunsAsync("test", 50);

        // The run that copied the file, and the latest of the four that found nothing to do.
        Assert.Equal(2, runs.Count);
        Assert.Equal((0L, 1L), (runs[0].Copied, runs[1].Copied));
        Assert.True(runs[0].StartedUtc >= runs[1].StartedUtc);
    }

    [SkippableFact]
    public async Task A_changed_file_replaces_its_copy_and_the_old_copy_goes_to_history()
    {
        Env.Write("a.txt", "one");
        Env.Write("same.txt", "same");
        var (job, disk) = Setup();
        await RunAsync(job);

        Env.Write("a.txt", "two, and longer");
        var summary = await RunAsync(job);

        Assert.Equal(RunStatus.Ok, summary.Status);
        Assert.Equal(1, summary.Copied);
        Assert.Equal("two, and longer", File.ReadAllText(Current(disk, "a.txt")));
        var kept = Assert.Single(HistoryFiles(disk));
        Assert.EndsWith("/" + Env.InBackup("a.txt"), kept);
        Assert.Equal("one", File.ReadAllText(Path.Combine(disk, BackupLayout.History, kept)));
    }

    [SkippableFact]
    public async Task A_deleted_file_moves_to_history_and_its_emptied_folders_are_removed()
    {
        Env.Write("keep.txt", "keep");
        Env.Write("gone/deep/x.txt", "x");
        var (job, disk) = Setup();
        await RunAsync(job);

        Directory.Delete(Env.PathOf("gone"), recursive: true);
        var summary = await RunAsync(job);

        Assert.Equal(RunStatus.Ok, summary.Status);
        Assert.Equal(1, summary.Removed);
        Assert.Equal(InBackup("keep.txt"), CurrentFiles(disk));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(Current(disk, "gone/deep/x.txt")))));
        Assert.EndsWith("/" + Env.InBackup("gone/deep/x.txt"), Assert.Single(HistoryFiles(disk)));
    }

    [SkippableFact]
    public async Task Without_history_a_change_overwrites_and_a_deletion_deletes()
    {
        Env.Write("a.txt", "one");
        Env.Write("b.txt", "bee");
        var (job, disk) = Setup(keepHistoryDays: 0);
        await RunAsync(job);

        Env.Write("a.txt", "two, and longer");
        File.Delete(Env.PathOf("b.txt"));
        var summary = await RunAsync(job);

        Assert.Equal(RunStatus.Ok, summary.Status);
        Assert.Equal((1, 1), (summary.Copied, summary.Removed));
        Assert.Equal(InBackup("a.txt"), CurrentFiles(disk));
        Assert.Equal("two, and longer", File.ReadAllText(Current(disk, "a.txt")));
        Assert.Empty(HistoryFiles(disk));
    }

    [SkippableFact]
    public async Task A_file_whose_time_changed_but_not_its_content_is_not_sent_again()
    {
        var source = Env.Write("a.txt", "alpha");
        var (job, _) = Setup();
        await RunAsync(job);

        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(1));
        var trouble = Trouble(job);
        var summary = await Env.Runner(_ => trouble).RunAsync(job, false, CancellationToken.None);

        Assert.Equal(RunStatus.Ok, summary.Status);
        Assert.Equal((0, 1), (summary.Copied, summary.SameContent));
        Assert.Empty(trouble.Writes);

        // The new time was recorded, so the file is not even looked at the next time.
        var again = await RunAsync(job);
        Assert.Equal((0, 0), (again.Copied, again.SameContent));
    }

    [SkippableFact]
    public async Task A_source_that_is_missing_is_not_taken_for_deleted_files()
    {
        Env.Write("a.txt", "alpha");
        var (job, disk) = Setup();
        await RunAsync(job);

        // As if the drive holding the source were unplugged.
        Directory.Move(Env.SourceDir, Env.SourceDir + "-away");
        var summary = await RunAsync(job);

        Assert.Equal(RunStatus.Errors, summary.Status);
        Assert.Equal(0, summary.Removed);
        Assert.Contains("was not found", summary.Message);
        Assert.Equal(InBackup("a.txt"), CurrentFiles(disk));

        Directory.Move(Env.SourceDir + "-away", Env.SourceDir);
        Assert.Equal(RunStatus.Ok, (await RunAsync(job)).Status);
    }

    [SkippableFact]
    public async Task A_lost_database_is_rebuilt_from_what_the_destination_holds()
    {
        var long_ago = new DateTime(2023, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        foreach (var name in new[] { "a.txt", "sub/b.txt", "sub/deep/c.txt" })
            File.SetLastWriteTimeUtc(Env.Write(name, "content of " + name), long_ago);
        var (job, disk) = Setup();
        await RunAsync(job);

        await Env.LoseStateAsync();
        Env.Write("new.txt", "new");
        var trouble = Trouble(job);
        var summary = await Env.Runner(_ => trouble).RunAsync(job, false, CancellationToken.None);

        Assert.Equal(RunStatus.Ok, summary.Status);
        Assert.Equal(3, summary.Adopted);
        Assert.Equal(1, summary.Copied);
        Assert.Equal(BackupLayout.Partial + "/" + BackupRunner.PartialName(Env.InBackup("new.txt")), Assert.Single(trouble.Writes).Path);
        Assert.Empty(HistoryFiles(disk));
    }

    [SkippableFact]
    public async Task A_large_file_that_was_cut_off_continues_where_it_stopped()
    {
        var source = Env.WriteBytes("big.bin", 600_000, seed: 7);
        var (job, disk) = Setup();
        var trouble = Trouble(job);
        trouble.WritesToCut = 1;
        trouble.CutAfterBytes = 250_000;
        var runner = Env.Runner(_ => trouble);
        runner.ResumeThreshold = 1000;

        var summary = await runner.RunAsync(job, false, CancellationToken.None);

        Assert.Equal(RunStatus.Ok, summary.Status);
        Assert.Equal(1, summary.Copied);
        var writes = trouble.Writes.ToList();
        Assert.Equal(2, writes.Count);
        Assert.Equal(0, writes[0].Offset);
        Assert.InRange(writes[1].Offset, 1, 250_000);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Current(disk, "big.bin")));
        Assert.Empty(FilesUnder(Path.Combine(disk, BackupLayout.Partial)));
    }

    [SkippableFact]
    public async Task A_cut_off_copy_starts_over_when_the_source_changed_meanwhile()
    {
        var source = Env.WriteBytes("big.bin", 300_000, seed: 1);
        var (job, disk) = Setup();
        var trouble = Trouble(job);
        trouble.WritesToCut = 3; // every attempt of the first run
        trouble.CutAfterBytes = 100_000;
        var runner = Env.Runner(_ => trouble);
        runner.ResumeThreshold = 1000;
        var first = await runner.RunAsync(job, false, CancellationToken.None);
        Assert.Equal(1, first.Failed);

        // Same size, different bytes and time: the part already sent no longer belongs to this file.
        Env.WriteBytes("big.bin", 300_000, seed: 2);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(5));
        trouble.Writes.Clear();
        var second = await runner.RunAsync(job, false, CancellationToken.None);

        Assert.Equal(RunStatus.Ok, second.Status);
        Assert.Equal(0, Assert.Single(trouble.Writes).Offset);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Current(disk, "big.bin")));
    }

    [SkippableFact]
    public async Task A_run_that_loses_its_destination_stops_and_the_next_run_finishes_the_job()
    {
        for (var i = 0; i < 12; i++) Env.Write($"f{i:00}.txt", "file " + i);
        var (job, disk) = Setup();
        var trouble = Trouble(job);
        trouble.GoOutOfReachAfterWrites = 4;
        var runner = Env.Runner(_ => trouble);
        runner.GiveUpAfter = 2;

        var first = await runner.RunAsync(job, false, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, first.Status);
        Assert.Contains("stopped answering", first.Message);
        Assert.InRange(first.Copied, 1, 11);
        Assert.Equal(first.Copied, CurrentFiles(disk).Count);

        trouble.OutOfReach = false;
        trouble.GoOutOfReachAfterWrites = null;
        var second = await runner.RunAsync(job, false, CancellationToken.None);

        Assert.Equal(RunStatus.Ok, second.Status);
        Assert.Equal(12 - first.Copied, second.Copied);
        Assert.Equal(12, CurrentFiles(disk).Count);
    }

    [SkippableFact]
    public async Task A_destination_that_limits_connections_gets_as_many_as_it_allows()
    {
        for (var i = 0; i < 30; i++) Env.Write($"f{i:00}.txt", "file " + i);
        var (job, disk) = Setup();
        var trouble = Trouble(job);
        trouble.MaxConnections = 2; // The backup asks for three at a time.

        var summary = await Env.Runner(_ => trouble).RunAsync(job, false, CancellationToken.None);

        Assert.Equal(RunStatus.Ok, summary.Status);
        Assert.Equal((30, 0), (summary.Copied, summary.Failed));
        Assert.Equal(30, CurrentFiles(disk).Count);
        Assert.InRange(trouble.MostOpenAtOnce, 1, 2);
    }

    [SkippableFact]
    public async Task A_destination_that_cannot_be_reached_fails_the_run_with_the_reason()
    {
        Env.Write("a.txt", "alpha");
        var (job, _) = Setup();
        var trouble = Trouble(job);
        trouble.OutOfReach = true;

        var summary = await Env.Runner(_ => trouble).RunAsync(job, false, CancellationToken.None);

        Assert.Equal(RunStatus.Failed, summary.Status);
        Assert.Contains("out of reach", summary.Message);
        Assert.Equal("failed", (await Env.State.GetLastRunsAsync("test", 1))[0].Status);
    }

    [SkippableFact]
    public async Task Restore_brings_back_exactly_what_was_backed_up()
    {
        Env.Write("a.txt", "alpha");
        Env.Write("sub/b.cs", "beta");
        Env.WriteBytes("sub/deep/data.bin", 200_000, seed: 3);
        Env.Write("empty.txt", "");
        var (job, _) = Setup();
        await RunAsync(job);

        var target = Path.Combine(Env.Root, "restored");
        var restorer = new Restorer(Env.State, Env.Log);
        var result = await restorer.RestoreAsync(job, null, null, target, CancellationToken.None);

        Assert.Equal((4, 0), (result.Restored, result.Failed));
        foreach (var relative in new[] { "a.txt", "sub/b.cs", "sub/deep/data.bin", "empty.txt" })
            Assert.Equal(File.ReadAllBytes(Env.PathOf(relative)),
                File.ReadAllBytes(Path.Combine(target, Env.InBackup(relative).Replace('/', Path.DirectorySeparatorChar))));

        // Restoring again finds everything in place.
        var again = await restorer.RestoreAsync(job, null, null, target, CancellationToken.None);
        Assert.Equal((0, 4), (again.Restored, again.AlreadyThere));

        // One folder, named by its original path.
        var part = Path.Combine(Env.Root, "restored-sub");
        var some = await restorer.RestoreAsync(job, Env.PathOf("sub"), null, part, CancellationToken.None);
        Assert.Equal(2, some.Restored);
        Assert.Equal(InBackup("sub/b.cs", "sub/deep/data.bin"), FilesUnder(part));
    }

    [SkippableFact]
    public async Task Empty_folders_are_part_of_the_backup_and_of_a_restore()
    {
        // A git repository keeps empty folders it can't do without, such as refs/heads once its branches are packed.
        Env.Write("repo/HEAD", "ref: refs/heads/main");
        Env.Write("docs/x.txt", "x");
        Directory.CreateDirectory(Env.PathOf("repo/refs/heads"));
        Directory.CreateDirectory(Env.PathOf("repo/refs/tags"));
        Directory.CreateDirectory(Env.PathOf("only-excluded/node_modules"));
        var (job, disk) = Setup(30, "node_modules/");

        Assert.Equal(RunStatus.Ok, (await RunAsync(job)).Status);

        Assert.True(Directory.Exists(Current(disk, "repo/refs/heads")));
        Assert.True(Directory.Exists(Current(disk, "repo/refs/tags")));
        Assert.True(Directory.Exists(Current(disk, "only-excluded")));
        Assert.False(Directory.Exists(Current(disk, "only-excluded/node_modules")));

        var target = Path.Combine(Env.Root, "restored");
        await new Restorer(Env.State, Env.Log).RestoreAsync(job, null, null, target, CancellationToken.None);
        Assert.True(Directory.Exists(Path.Combine(target, Env.InBackup("repo/refs/tags").Replace('/', Path.DirectorySeparatorChar))));

        // One empty folder is deleted, one gets a file, and one folder becomes empty.
        Directory.Delete(Env.PathOf("repo/refs/tags"));
        Env.Write("repo/refs/heads/main", "0123abcd");
        File.Delete(Env.PathOf("docs/x.txt"));
        Assert.Equal(RunStatus.Ok, (await RunAsync(job)).Status);

        Assert.False(Directory.Exists(Current(disk, "repo/refs/tags")));
        Assert.Equal("0123abcd", File.ReadAllText(Current(disk, "repo/refs/heads/main")));
        Assert.True(Directory.Exists(Current(disk, "docs")));
        Assert.Empty(Directory.GetFileSystemEntries(Current(disk, "docs")));

        // After that the backup is settled: the next run has nothing to do.
        var trouble = Trouble(job);
        trouble.OutOfReach = true;
        Assert.Equal(RunStatus.Ok, (await Env.Runner(_ => trouble).RunAsync(job, false, CancellationToken.None)).Status);
        Assert.Equal(0, trouble.Connections);
    }

    [SkippableFact]
    public async Task Restore_can_bring_back_the_copy_a_run_set_aside()
    {
        Env.Write("a.txt", "the first version");
        var (job, disk) = Setup();
        await RunAsync(job);
        Env.Write("a.txt", "v2");
        await RunAsync(job);

        var run = Path.GetFileName(Assert.Single(Directory.GetDirectories(Path.Combine(disk, BackupLayout.History))));
        var target = Path.Combine(Env.Root, "restored");
        var result = await new Restorer(Env.State, Env.Log).RestoreAsync(job, null, run, target, CancellationToken.None);

        Assert.Equal(1, result.Restored);
        Assert.Equal("the first version", File.ReadAllText(Path.Combine(target, Env.InBackup("a.txt").Replace('/', Path.DirectorySeparatorChar))));
    }

    [SkippableFact]
    public async Task Verify_finds_a_copy_that_went_missing_and_fix_has_it_copied_again()
    {
        Env.Write("a.txt", "alpha");
        Env.Write("sub/b.txt", "beta");
        var (job, disk) = Setup();
        await RunAsync(job);
        var verifier = new Verifier(Env.State, Env.Log);
        Assert.True((await verifier.VerifyAsync(job, fix: false, CancellationToken.None)).Ok);

        File.Delete(Current(disk, "sub/b.txt"));
        File.WriteAllText(Current(disk, "a.txt"), "tampered with");

        var found = await verifier.VerifyAsync(job, fix: true, CancellationToken.None);
        Assert.Equal(2, found.Checked);
        Assert.Equal(InBackup("sub/b.txt"), found.Missing);
        Assert.Equal(InBackup("a.txt"), found.WrongSize);

        var summary = await RunAsync(job);
        Assert.Equal(2, summary.Copied);
        Assert.Equal("alpha", File.ReadAllText(Current(disk, "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Current(disk, "sub/b.txt")));
        Assert.True((await verifier.VerifyAsync(job, fix: false, CancellationToken.None)).Ok);
    }

    [SkippableFact]
    public async Task A_file_that_cannot_be_read_is_reported_and_the_rest_is_still_copied()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Needs a file locked against reading, which Windows does.");
        Env.Write("a.txt", "alpha");
        var locked = Env.Write("locked.txt", "busy");
        var (job, disk) = Setup();

        RunSummary summary;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            summary = await RunAsync(job);

        Assert.Equal(RunStatus.Errors, summary.Status);
        Assert.Equal((1, 1), (summary.Copied, summary.Failed));
        Assert.Contains("locked.txt", summary.Message);
        Assert.Equal(InBackup("a.txt"), CurrentFiles(disk));

        var later = await RunAsync(job);
        Assert.Equal((RunStatus.Ok, 1), (later.Status, later.Copied));
    }

    [SkippableFact]
    public async Task A_file_renamed_only_in_its_letter_case_ends_up_under_the_new_name()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Needs a file system that ignores case.");
        Env.Write("Readme.txt", "read me");
        var (job, disk) = Setup();
        await RunAsync(job);

        File.Move(Env.PathOf("Readme.txt"), Env.PathOf("README.txt"));
        var summary = await RunAsync(job);

        Assert.Equal(RunStatus.Ok, summary.Status);
        var only = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Current(disk, "README.txt"))!));
        Assert.Equal("README.txt", Path.GetFileName(only));
        Assert.Equal("read me", File.ReadAllText(only));
    }

    [SkippableFact]
    public async Task History_older_than_the_limit_is_deleted()
    {
        Env.Write("a.txt", "alpha");
        var (job, disk) = Setup(keepHistoryDays: 30);
        var old = Path.Combine(disk, BackupLayout.History, "2020-01-01_000000", "C", "x");
        var recent = Path.Combine(disk, BackupLayout.History, DateTime.Now.AddDays(-2).ToString(BackupLayout.StampFormat), "C", "x");
        var notOurs = Path.Combine(disk, BackupLayout.History, "notes");
        foreach (var folder in new[] { old, recent, notOurs })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "f.txt"), "kept copy");
        }

        var summary = await RunAsync(job);

        Assert.Equal(RunStatus.Ok, summary.Status);
        Assert.False(Directory.Exists(Path.Combine(disk, BackupLayout.History, "2020-01-01_000000")));
        Assert.True(File.Exists(Path.Combine(recent, "f.txt")));
        Assert.True(File.Exists(Path.Combine(notOurs, "f.txt")));
    }

    [SkippableFact]
    public async Task A_dry_run_changes_nothing()
    {
        Env.Write("a.txt", "alpha");
        var (job, disk) = Setup();

        var summary = await Env.Runner().RunAsync(job, dryRun: true, CancellationToken.None);

        Assert.Contains("1 file to copy", summary.Plan[0]);
        Assert.Empty(CurrentFiles(disk));
        Assert.Empty(await Env.State.GetLastRunsAsync("test", 1));
    }

    [SkippableFact]
    public async Task A_run_is_skipped_while_another_one_writes_to_the_same_destination()
    {
        Env.Write("a.txt", "alpha");
        var (job, disk) = Setup();

        using (RunLock.TryAcquire(Env.Paths.LockDir, job.Destination.Id))
            Assert.Equal(RunStatus.Skipped, (await RunAsync(job)).Status);

        Assert.Empty(CurrentFiles(disk));
        Assert.Equal(RunStatus.Ok, (await RunAsync(job)).Status);
    }

    [SkippableFact]
    public async Task Many_files_in_many_folders_all_arrive()
    {
        var expected = new List<string>();
        for (var folder = 0; folder < 20; folder++)
            for (var file = 0; file < 15; file++)
            {
                var relative = $"p{folder:00}/src/f{file:00}.txt";
                Env.Write(relative, $"{folder}-{file}");
                expected.Add(relative);
            }
        var (job, disk) = Setup();

        var summary = await RunAsync(job);

        Assert.Equal(RunStatus.Ok, summary.Status);
        Assert.Equal(300, summary.Copied);
        Assert.Equal(InBackup([.. expected]), CurrentFiles(disk));
        Assert.Equal("7-3", File.ReadAllText(Current(disk, "p07/src/f03.txt")));
    }
}

public sealed class LocalFolderBackupTests(ITestOutputHelper output) : BackupTests(output)
{
    protected override string? Unavailable => null;

    protected override DestinationSpec NewDestination()
    {
        var folder = Path.Combine(Env.Root, "dest");
        Directory.CreateDirectory(folder);
        return new DestinationSpec { Root = folder };
    }

    protected override string DiskFolder(DestinationSpec destination) => destination.Root;

    [Fact]
    public async Task A_destination_folder_that_does_not_exist_is_not_created()
    {
        Env.Write("a.txt", "alpha");
        var missing = Path.Combine(Env.Root, "unplugged", "backups");
        var job = Env.Job(new DestinationSpec { Root = missing });

        var summary = await RunAsync(job);

        Assert.Equal(RunStatus.Failed, summary.Status);
        Assert.Contains("doesn't exist", summary.Message);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task A_destination_inside_a_source_is_not_backed_up_into_itself()
    {
        Env.Write("a.txt", "alpha");
        var inside = Path.Combine(Env.SourceDir, "backups");
        Directory.CreateDirectory(inside);
        var job = Env.Job(new DestinationSpec { Root = inside });

        await RunAsync(job);
        var second = await RunAsync(job);

        Assert.Equal((RunStatus.Ok, 0, 1), (second.Status, second.Copied, second.Scanned));
        Assert.Equal(InBackup("a.txt"), CurrentFiles(inside));
    }

    [SkippableFact]
    public void A_link_is_not_followed()
    {
        Env.Write("real/a.txt", "alpha");
        try
        {
            Directory.CreateSymbolicLink(Env.PathOf("link"), Env.PathOf("real"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, "This account may not create symbolic links.");
        }

        var scan = new SourceScanner().Scan(Env.Job(new DestinationSpec { Root = Env.Root }), [], CancellationToken.None);

        Assert.Equal(InBackup("real/a.txt"), scan.Files.Select(f => f.BackupPath).ToList());
        Assert.Equal(1, scan.Links);
    }

    [Fact]
    public void A_folder_that_cannot_be_listed_protects_what_was_backed_up_from_it()
    {
        var scan = new ScanResult();
        scan.MarkUnreadable("C/code/app/private", "no access");

        Assert.True(scan.IsUnreadable("C/code/app/private"));
        Assert.True(scan.IsUnreadable("C/code/app/private/deep/a.txt"));
        Assert.False(scan.IsUnreadable("C/code/app/private-notes/a.txt"));
        Assert.False(scan.IsUnreadable("C/code/app/a.txt"));
    }
}

public sealed class SftpBackupTests(ITestOutputHelper output, SftpServerFixture server) : BackupTests(output), IClassFixture<SftpServerFixture>
{
    protected override string? Unavailable => server.Unavailable;

    protected override DestinationSpec NewDestination() => server.Destination("backup-" + Guid.NewGuid().ToString("N")[..8]);

    protected override string DiskFolder(DestinationSpec destination)
        => Path.Combine(server.ServedFolder, destination.Root.Trim('/'));

    [SkippableFact]
    public async Task The_servers_host_key_is_remembered_and_a_different_one_is_refused()
    {
        Env.Write("a.txt", "alpha");
        var (job, _) = Setup();
        Assert.Equal(RunStatus.Ok, (await RunAsync(job)).Status);

        var remembered = await Env.State.GetHostKeyAsync("127.0.0.1", server.Port);
        Assert.StartsWith("SHA256:", remembered);

        // The right key, written in the configuration, is accepted.
        Env.Write("b.txt", "beta");
        var pinned = job with { Destination = job.Destination with { HostKey = remembered } };
        Assert.Equal(RunStatus.Ok, (await RunAsync(pinned)).Status);

        // Any other key is not, and nothing is sent.
        Env.Write("c.txt", "gamma");
        var wrong = job with { Destination = job.Destination with { HostKey = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" } };
        var refused = await RunAsync(wrong);
        Assert.Equal(RunStatus.Failed, refused.Status);
        Assert.Contains("in the configuration", refused.Message);
        Assert.Contains(remembered!, refused.Message);
        Assert.Equal(InBackup("a.txt", "b.txt"), CurrentFiles(DiskFolder(job.Destination)));
    }

    [SkippableFact]
    public async Task A_server_whose_key_changed_since_the_first_connection_is_refused()
    {
        Env.Write("a.txt", "alpha");
        var (job, disk) = Setup();
        // As if hback had met this server before, when it had another key.
        await Env.State.AddHostKeyAsync("127.0.0.1", server.Port, "SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");

        var refused = await RunAsync(job);

        Assert.Equal(RunStatus.Failed, refused.Status);
        Assert.Contains("than on hback's first connection", refused.Message);
        Assert.Contains("<host_key>SHA256:", refused.Message);
        Assert.Empty(CurrentFiles(disk));
    }

    [SkippableFact]
    public async Task A_wrong_password_fails_the_run_with_the_reason()
    {
        Env.Write("a.txt", "alpha");
        var (job, _) = Setup();
        var wrong = job with { Destination = job.Destination with { Password = "not the password" } };

        var summary = await RunAsync(wrong);

        Assert.Equal(RunStatus.Failed, summary.Status);
        Assert.Contains("refused the login", summary.Message);
    }

    [SkippableFact]
    public async Task A_backup_can_live_under_the_login_folder()
    {
        Env.Write("a.txt", "alpha");
        Skip.If(Unavailable is not null, Unavailable);
        var folder = "home-" + Guid.NewGuid().ToString("N")[..8];
        var job = Env.Job(server.Destination(folder) with { Root = folder + "/nested/backup" });

        var summary = await RunAsync(job);

        Assert.Equal(RunStatus.Ok, summary.Status);
        Assert.Equal(InBackup("a.txt"), CurrentFiles(Path.Combine(server.ServedFolder, folder, "nested", "backup")));
    }
}
