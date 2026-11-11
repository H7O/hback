using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HBack.Config;
using HBack.Destinations;
using HBack.Scan;
using HBack.State;
using Renci.SshNet.Common;

namespace HBack.Engine;

/// <summary>The three folders hback keeps inside a backup's destination.</summary>
public static class BackupLayout
{
    /// <summary>The files as they are now. Everything in here is a complete file.</summary>
    public const string Current = "current";

    /// <summary>Previous copies of changed and deleted files, one folder per run that replaced any.</summary>
    public const string History = "history";

    /// <summary>Files on their way in. A file moves to <see cref="Current"/> only when it is whole.</summary>
    public const string Partial = "partial";

    public const string StampFormat = "yyyy-MM-dd_HHmmss";

    public static bool TryParseStamp(string folderName, out DateTime when)
    {
        when = default;
        return folderName.Length >= StampFormat.Length
            && DateTime.TryParseExact(folderName[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out when);
    }
}

/// <summary>
/// Runs one backup once: finds what changed since the last run, and copies only that.
/// <para>
/// A file reaches the destination in two steps. It is written into <c>partial/</c>, and when it is
/// whole it is moved into <c>current/</c>. Only then is it recorded in the state database. So a run
/// can be cut off at any point: what is recorded is complete, and the next run carries on with the rest.
/// </para>
/// </summary>
public sealed class BackupRunner
{
    private const int MaxAttempts = 3;

    private readonly AppPaths _paths;
    private readonly StateStore _state;
    private readonly ILogger _log;
    private readonly Func<BackupJob, IDestinationFactory> _factories;

    public BackupRunner(AppPaths paths, StateStore state, ILogger log, Func<BackupJob, IDestinationFactory>? factories = null)
    {
        _paths = paths;
        _state = state;
        _log = log;
        _factories = factories ?? (job => DestinationFactory.For(job.Destination, state, log));
    }

    /// <summary>A cut-off copy of a file at least this large is continued. A smaller one starts again.</summary>
    internal long ResumeThreshold { get; set; } = 8L * 1024 * 1024;

    /// <summary>The waits between the attempts at one file.</summary>
    internal TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    /// <summary>The run stops when this many files in a row fail while the destination is out of reach.</summary>
    internal int GiveUpAfter { get; set; } = 3;

    internal TimeSpan ProgressEvery { get; set; } = TimeSpan.FromSeconds(30);

    public async Task<RunSummary> RunAsync(BackupJob job, bool dryRun, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var summary = new RunSummary { Backup = job.Name };

        using var runLock = RunLock.TryAcquire(_paths.LockDir, job.Destination.Id, TimeSpan.FromSeconds(1));
        if (runLock is null)
        {
            summary.Status = RunStatus.Skipped;
            summary.Message = "Another hback run is writing to this destination, so this one was skipped.";
            return summary;
        }

        var destId = await _state.GetDestinationIdAsync(job.Destination.Id);
        long? runId = dryRun ? null : await _state.StartRunAsync(job.Name, destId);
        await using var run = new Run(job, destId, runId, _factories(job), ct);

        try
        {
            await RunCoreAsync(run, summary, dryRun);
            summary.Status = run.Failed > 0 || summary.Unreadable > 0 || run.Problems > 0 ? RunStatus.Errors : RunStatus.Ok;
            if (summary.Status == RunStatus.Errors) summary.Message = run.FirstProblem;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            summary.Status = RunStatus.Cancelled;
            summary.Message = "Stopped before it finished. The next run carries on from here.";
        }
        catch (Exception ex)
        {
            summary.Status = RunStatus.Failed;
            summary.Message = (run.AbortReason ?? ex).Message;
        }

        summary.Copied = run.Copied;
        summary.CopiedBytes = run.CopiedBytes;
        summary.Removed = run.Removed;
        summary.Failed = run.Failed;
        summary.SameContent = run.SameContent;
        summary.Elapsed = watch.Elapsed;

        if (runId is not null)
            await _state.FinishRunAsync(job.Name, runId.Value, summary.Status.ToString().ToLowerInvariant(), summary.Scanned,
                summary.Copied, summary.CopiedBytes, summary.Removed, summary.Failed, summary.Message);
        return summary;
    }

    private async Task RunCoreAsync(Run run, RunSummary summary, bool dryRun)
    {
        var job = run.Job;
        var ct = run.Token;

        // 1. What is on this machine now.
        var skip = new List<string> { _paths.DataDir };
        if (!job.Destination.IsSftp) skip.Add(job.Destination.Root);
        var scan = await Task.Run(() => new SourceScanner().Scan(job, skip, ct), ct);
        summary.Scanned = scan.Files.Count;
        summary.Unreadable = scan.Problems.Count;
        foreach (var problem in scan.Problems)
        {
            run.FirstProblem ??= problem;
            _log.LogWarning("{Backup}: {Problem}", job.Name, problem);
        }

        // 2. What the destination holds, as recorded by earlier runs.
        run.Known = await _state.LoadFilesAsync(run.DestId);
        if (run.Known.Count == 0 && scan.Files.Count > 0 && !dryRun)
        {
            summary.Adopted = await AdoptAsync(run, scan);
            if (summary.Adopted > 0) run.Known = await _state.LoadFilesAsync(run.DestId);
        }

        // 3. The difference.
        var toCopy = new List<SourceFile>();
        var scanned = new HashSet<string>(scan.Files.Count, StringComparer.Ordinal);
        foreach (var file in scan.Files)
        {
            scanned.Add(file.BackupPath);
            if (!run.Known.TryGetValue(file.BackupPath, out var known) || known.Size != file.Size || known.ModifiedTicks != file.ModifiedTicks)
                toCopy.Add(file);
        }
        // A file hback has but did not find is taken as deleted, unless its source or folder could not be read.
        var toRemove = run.Known.Keys.Where(p => !scanned.Contains(p) && !scan.IsUnreadable(p)).ToList();

        // Empty folders are kept too, so that a restore gives back the same tree.
        var knownFolders = await _state.LoadFoldersAsync(run.DestId);
        var emptyFolders = new HashSet<string>(scan.EmptyFolders, StringComparer.Ordinal);
        var foldersToCreate = scan.EmptyFolders.Where(f => !knownFolders.Contains(f)).ToList();
        var foldersToForget = knownFolders.Where(f => !emptyFolders.Contains(f) && !scan.IsUnreadable(f)).ToList();

        if (dryRun)
        {
            summary.Plan.Add($"{Text.Count(toCopy.Count, "file")} to copy ({Text.Bytes(toCopy.Sum(f => f.Size))}), {Text.Count(toRemove.Count, "file")} to remove, {Text.Count(scan.Files.Count - toCopy.Count, "file")} up to date.");
            if (toCopy.Count <= 25)
            {
                summary.Plan.AddRange(toCopy.Select(f => "  copy    " + f.FullPath));
            }
            else
            {
                // Too many to list. Where the space goes is what helps decide what to exclude.
                summary.Plan.Add("  Where the space goes:");
                summary.Plan.AddRange(toCopy
                    .GroupBy(f => TopFolder(job, f.FullPath), BackupJob.PathComparer)
                    .Select(g => (Folder: g.Key, Bytes: g.Sum(f => f.Size), Files: g.Count()))
                    .OrderByDescending(g => g.Bytes)
                    .Take(12)
                    .Select(g => $"  {Text.Bytes(g.Bytes),10}  {Text.Count(g.Files, "file"),13}  {g.Folder}"));
            }
            summary.Plan.AddRange(toRemove.Take(25).Select(p => "  remove  " + p));
            if (toRemove.Count > 25) summary.Plan.Add($"  ... and {toRemove.Count - 25} more");
            if (scan.Excluded > 0) summary.Plan.Add($"  {Text.Count(scan.Excluded, "file or folder", "files and folders")} left out by the exclude patterns.");
            if (scan.Links > 0) summary.Plan.Add($"  {Text.Count(scan.Links, "link")} not followed.");
            return;
        }

        var pruneDue = job.KeepHistoryDays > 0
            && (await _state.GetLastPrunedAsync(job.Destination.Id) is not { } pruned || DateTime.UtcNow - pruned > TimeSpan.FromHours(20));
        if (toCopy.Count == 0 && toRemove.Count == 0 && foldersToCreate.Count == 0 && foldersToForget.Count == 0 && !pruneDue)
            return; // Nothing changed, so the destination is not even contacted.

        // 4. Make the destination match. Removals go first: when a file was only renamed in its
        // letter case, a destination that ignores case would otherwise lose the new copy with the old.
        run.LogEachFile = toCopy.Count + toRemove.Count <= 50;
        await run.WithSessionAsync(async session =>
        {
            await session.CreateFolderAsync(BackupLayout.Current, ct);
            await session.CreateFolderAsync(BackupLayout.Partial, ct);
            if (job.KeepHistoryDays > 0)
                run.Stamp = await PickStampAsync(session, ct);
        });

        if (toCopy.Count > 50)
            _log.LogInformation("{Backup}: {Files} to copy ({Bytes}) to {Destination}.", job.Name, Text.Count(toCopy.Count, "file"),
                Text.Bytes(toCopy.Sum(f => f.Size)), job.Destination.Display);

        await ForEachAsync(run, toRemove, RemoveOneAsync, path => path, "remove");
        await ForEachAsync(run, toCopy, CopyOneAsync, file => file.FullPath, "copy", toCopy.Count);

        // 5. Empty folders, then tidy up. No file depends on any of this, so when it fails the run
        // is reported as having had a problem, not as failed, and the next run does it again.
        try
        {
            await run.WithSessionAsync(async session =>
            {
                foreach (var folder in foldersToCreate)
                {
                    var at = BackupLayout.Current + "/" + folder;
                    try
                    {
                        await session.CreateFolderAsync(at, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A file sits where the folder belongs. It goes the way of any file that is no longer in the sources.
                        if (await session.StatAsync(at, ct) is not { IsFolder: false }) throw;
                        await SetAsideAsync(run, session, folder);
                        await session.CreateFolderAsync(at, ct);
                    }
                    await _state.AddFolderAsync(run.DestId, folder);
                }
                foreach (var folder in foldersToForget)
                {
                    // It has files in it now, or it is gone from the source. Only in the second case
                    // does removing it below succeed, because only then is it empty.
                    await _state.DeleteFolderAsync(run.DestId, folder);
                    run.EmptiedFolders[folder] = 0;
                }

                await DeleteEmptiedFoldersAsync(run, session, emptyFolders);
                if (run.Failed == 0)
                {
                    // Every copy finished, so whatever is left in partial/ belongs to files that no longer exist.
                    foreach (var leftover in await session.ListAsync(BackupLayout.Partial, ct))
                        if (!leftover.IsFolder)
                            await session.DeleteFileAsync(BackupLayout.Partial + "/" + leftover.Name, ct);
                    await _state.DeleteAllPartialsAsync(run.DestId);
                }
                if (pruneDue)
                    await PruneHistoryAsync(run, session);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.NoteProblem($"The files were copied, but tidying up at the destination did not finish: {ex.Message}");
            _log.LogWarning("{Backup}: the files were copied, but tidying up at the destination did not finish: {Message}", job.Name, ex.Message);
        }
    }

    #region one file

    private async Task CopyOneAsync(Run run, IDestination session, SourceFile file)
    {
        var ct = run.Token;
        var path = file.BackupPath;

        // The size and time are read again here, because the scan may be minutes old by now.
        var info = new FileInfo(file.FullPath);
        if (!info.Exists) return; // Deleted since the scan. The next run removes it from the backup.
        var size = info.Length;
        var modified = info.LastWriteTimeUtc.Ticks;
        run.Known.TryGetValue(path, out var known);

        FileStream source;
        try
        {
            // The sharing flags let a file be read while another program has it open for writing.
            source = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SourceFileException(ex.Message, ex);
        }

        await using (source)
        {
            // Same size, new time: often the content is the same (a branch switched and switched back,
            // a build that rewrote identical files). Reading the file locally is cheaper than sending it.
            if (known is { Sha256: not null } && known.Size == size)
            {
                string localHash;
                try
                {
                    localHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, ct));
                }
                catch (IOException ex)
                {
                    throw new SourceFileException(ex.Message, ex);
                }
                if (localHash == known.Sha256)
                {
                    await _state.TouchFileAsync(run.DestId, path, modified);
                    run.CountSameContent();
                    return;
                }
                source.Position = 0;
            }

            var partial = BackupLayout.Partial + "/" + PartialName(path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // A large file that was cut off last time continues where it stopped, as long as the
            // source is still the file that copy was started from.
            long offset = 0;
            var resumable = size >= ResumeThreshold;
            if (resumable)
            {
                var started = await _state.GetPartialAsync(run.DestId, path);
                if (started is not null && started.Size == size && started.ModifiedTicks == modified
                    && await session.StatAsync(partial, ct) is { IsFolder: false } there && there.Size > 0 && there.Size <= size
                    && await HashPrefixAsync(source, there.Size, hash, ct))
                {
                    offset = there.Size;
                    _log.LogInformation("{Backup}: continuing {File} from {Done} of {Size}.", run.Job.Name, file.FullPath, Text.Bytes(offset), Text.Bytes(size));
                }
                else
                {
                    source.Position = 0;
                    hash.GetHashAndReset();
                    await _state.SetPartialAsync(run.DestId, path, size, modified);
                }
            }

            var reader = new HashingReadStream(source, hash);
            await session.WriteAsync(partial, reader, offset, ct);
            var total = offset + reader.BytesRead;
            var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());

            var arrived = await session.SealAsync(partial, new DateTime(modified, DateTimeKind.Utc), ct);
            if (arrived != total)
            {
                if (resumable) await _state.DeletePartialAsync(run.DestId, path);
                throw new IOException($"{Text.Bytes(arrived)} arrived at the destination where {Text.Bytes(total)} were sent.");
            }

            // The file is whole. Put it in place, keeping the copy it replaces if history is on.
            var final = BackupLayout.Current + "/" + path;
            if (known is not null)
                await SetAsideAsync(run, session, path);
            await session.CreateFolderAsync(PathText.ParentOf(final), ct);
            try
            {
                await session.MoveAsync(partial, final, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Something is already there that the state database doesn't know about.
                if (await session.StatAsync(final, ct) is null) throw;
                await SetAsideAsync(run, session, path);
                await session.MoveAsync(partial, final, ct);
            }

            // What is recorded is the size and time from before the copy. If the file changed while
            // it was being read, it differs from that now, and the next run copies it again.
            await _state.UpsertFileAsync(run.DestId, path, size, modified, sha256, run.RunId);
            if (resumable) await _state.DeletePartialAsync(run.DestId, path);

            info.Refresh();
            if (total != size || !info.Exists || info.Length != size || info.LastWriteTimeUtc.Ticks != modified)
                _log.LogWarning("{Backup}: {File} changed while it was being copied. It will be copied again on the next run.", run.Job.Name, file.FullPath);

            run.CountCopied(total - offset);
            if (run.LogEachFile)
                _log.LogInformation("{Backup}: copied {File} ({Size})", run.Job.Name, file.FullPath, Text.Bytes(total));
        }
    }

    private async Task RemoveOneAsync(Run run, IDestination session, string path)
    {
        await SetAsideAsync(run, session, path);
        await _state.DeleteFileAsync(run.DestId, path);
        run.EmptiedFolders[PathText.ParentOf(path)] = 0;
        run.CountRemoved();
        if (run.LogEachFile)
            _log.LogInformation("{Backup}: {Path} is no longer in the sources (deleted or now excluded), {Action}", run.Job.Name, path,
                run.Job.KeepHistoryDays > 0 ? "so its copy was moved to history" : "so its copy was deleted");
    }

    /// <summary>
    /// Takes a file out of <c>current/</c>: into this run's history folder when history is kept,
    /// otherwise it is deleted. Does nothing when the file isn't there.
    /// </summary>
    private static async Task SetAsideAsync(Run run, IDestination session, string path)
    {
        var ct = run.Token;
        var from = BackupLayout.Current + "/" + path;
        if (run.Job.KeepHistoryDays <= 0)
        {
            await session.DeleteFileAsync(from, ct);
            return;
        }

        var to = $"{BackupLayout.History}/{run.Stamp}/{path}";
        await session.CreateFolderAsync(PathText.ParentOf(to), ct);
        try
        {
            await session.MoveAsync(from, to, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (await session.StatAsync(from, ct) is null) return; // Nothing there to keep.
            // This run already set a copy of this file aside, so an attempt was repeated. The older
            // copy is the one worth keeping.
            if (await session.StatAsync(to, ct) is null) throw;
            await session.DeleteFileAsync(from, ct);
        }
    }

    #endregion

    #region workers

    /// <summary>
    /// Runs <paramref name="action"/> for every item, a few at a time, each worker on its own connection.
    /// One item failing does not stop the others.
    /// </summary>
    private async Task ForEachAsync<T>(Run run, IReadOnlyList<T> items, Func<Run, IDestination, T, Task> action,
        Func<T, string> describe, string verb, int reportTotal = 0)
    {
        if (items.Count == 0) return;

        using var progressStop = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        var progress = reportTotal > 50 ? ReportProgressAsync(run, reportTotal, progressStop.Token) : Task.CompletedTask;

        var queue = new ConcurrentQueue<T>(items);
        try
        {
            // One pass, normally. A second is needed only when a worker handed its item back (see
            // RunItemAsync) at the very moment the others ran out of work.
            while (!queue.IsEmpty)
            {
                var before = queue.Count;
                var workers = Enumerable.Range(0, Math.Min(run.Job.Parallel, before)).Select(_ => Task.Run(async () =>
                {
                    while (queue.TryDequeue(out var item))
                    {
                        if (await RunItemAsync(run, item, action, describe, verb)) continue;
                        queue.Enqueue(item);
                        return;
                    }
                })).ToList();
                await Task.WhenAll(workers);

                // Every pass must finish at least one item. This guards against going round forever.
                if (queue.Count >= before)
                    throw new DestinationException("The destination refused every new connection.");
            }
        }
        finally
        {
            progressStop.Cancel();
            await progress;
        }
    }

    /// <returns>False when this worker could not get a connection while others have one. The item
    /// is not done, and the caller hands it to the workers that are connected.</returns>
    private async Task<bool> RunItemAsync<T>(Run run, T item, Func<Run, IDestination, T, Task> action, Func<T, string> describe, string verb)
    {
        for (var attempt = 1; ; attempt++)
        {
            run.Token.ThrowIfCancellationRequested();
            IDestination? session = null;
            try
            {
                session = await run.RentAsync();
                await action(run, session, item);
                run.Return(session);
                run.DestinationWorked();
                return true;
            }
            catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
            {
                if (session is not null) await run.DiscardAsync(session);
                throw;
            }
            catch (SourceFileException ex)
            {
                // The file could not be read here. Trying again won't help, and the destination is not
                // to blame. Its connection is kept unless the read failed in the middle of an upload.
                if (session is not null)
                {
                    if (ex.DuringTransfer) await run.DiscardAsync(session);
                    else run.Return(session);
                }
                run.CountFailed($"Can't read '{describe(item)}': {ex.Message}");
                _log.LogWarning("{Backup}: can't read {Item}: {Message}", run.Job.Name, describe(item), ex.Message);
                return true;
            }
            catch (Exception ex)
            {
                // The destination took some connections and refused this one. Many servers limit how
                // many a user may open. That is no failure: the work goes on with the ones it allowed.
                if (session is null && run.LiveSessions > 0)
                {
                    if (run.NoteConnectionLimit())
                        _log.LogInformation("{Backup}: the destination allowed {Allowed} at a time and refused one more ({Message}), so files are copied {Count} at a time.",
                            run.Job.Name, Text.Count(run.LiveSessions, "connection"), ex.Message, run.LiveSessions);
                    return false;
                }

                // The connection may be in any state now, so it is dropped and the next attempt opens a new one.
                if (session is not null) await run.DiscardAsync(session);

                if (attempt < MaxAttempts && IsWorthRetrying(ex))
                {
                    var wait = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                    _log.LogWarning("{Backup}: could not {Verb} {Item}: {Message} Trying again in {Seconds} s.",
                        run.Job.Name, verb, describe(item), ex.Message, (int)wait.TotalSeconds);
                    await Task.Delay(wait, run.Token);
                    continue;
                }

                run.CountFailed($"Could not {verb} '{describe(item)}': {ex.Message}");
                _log.LogError("{Backup}: could not {Verb} {Item}: {Message}", run.Job.Name, verb, describe(item), ex.Message);

                // One bad file is not a reason to stop. A destination that is gone is: without this,
                // a run would grind through every remaining file, waiting on each.
                if (await run.DestinationIsReachableAsync())
                    run.DestinationWorked();
                else if (run.DestinationFailed() >= GiveUpAfter)
                    run.Abort(new DestinationException($"The destination stopped answering: {ex.Message} The next run carries on from here."));
                return true;
            }
        }
    }

    private static bool IsWorthRetrying(Exception ex)
        => ex is not (SftpPermissionDeniedException or UnauthorizedAccessException or PathTooLongException or NotSupportedException);

    private async Task ReportProgressAsync(Run run, int total, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(ProgressEvery);
            while (await timer.WaitForNextTickAsync(stop))
                _log.LogInformation("{Backup}: {Done} of {Total} files copied ({Bytes}).", run.Job.Name,
                    (run.Copied + run.SameContent + run.Failed).ToString("N0", CultureInfo.InvariantCulture),
                    total.ToString("N0", CultureInfo.InvariantCulture), Text.Bytes(run.CopiedBytes));
        }
        catch (OperationCanceledException)
        {
            // The phase is over.
        }
    }

    #endregion

    #region housekeeping

    /// <summary>
    /// When hback has no record of a destination that already holds files (its database was lost, or
    /// the machine was set up again), it takes stock instead of sending everything a second time: a
    /// file there with the same size and time as the source is recorded as backed up.
    /// </summary>
    private async Task<int> AdoptAsync(Run run, ScanResult scan)
    {
        var ct = run.Token;
        var found = new Dictionary<string, DestinationEntry>(StringComparer.Ordinal);
        await run.WithSessionAsync(async session =>
        {
            if (await session.StatAsync(BackupLayout.Current, ct) is null) return;
            _log.LogInformation("{Backup}: there is no record of this destination yet, but it holds files. Checking which of them are already up to date.", run.Job.Name);

            var pending = new Stack<string>();
            pending.Push("");
            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                foreach (var entry in await session.ListAsync(PathText.Join(BackupLayout.Current, folder), ct))
                {
                    var path = PathText.Join(folder, entry.Name);
                    if (entry.IsFolder) pending.Push(path);
                    else found[path] = entry;
                }
            }
        });
        if (found.Count == 0) return 0;

        // A destination keeps times to the second (SFTP) or to two seconds (FAT), so that is how they are compared.
        var same = scan.Files
            .Where(f => found.TryGetValue(f.BackupPath, out var there) && there.Size == f.Size
                && Math.Abs((there.ModifiedUtc - new DateTime(f.ModifiedTicks, DateTimeKind.Utc)).TotalSeconds) <= 2)
            .Select(f => (f.BackupPath, f.Size, f.ModifiedTicks))
            .ToList();
        if (same.Count > 0)
            await _state.UpsertFilesAsync(run.DestId, same, run.RunId);
        return same.Count;
    }

    private static async Task<string> PickStampAsync(IDestination session, CancellationToken ct)
    {
        var stamp = DateTime.Now.ToString(BackupLayout.StampFormat, CultureInfo.InvariantCulture);
        if (await session.StatAsync(BackupLayout.History + "/" + stamp, ct) is null) return stamp;
        // Two runs within the same second. Each gets its own folder, so neither overwrites the other's copies.
        for (var n = 2; ; n++)
            if (await session.StatAsync($"{BackupLayout.History}/{stamp}-{n}", ct) is null)
                return $"{stamp}-{n}";
    }

    /// <param name="keep">Folders that are empty at the source too, and so belong in the backup as they are.</param>
    private static async Task DeleteEmptiedFoldersAsync(Run run, IDestination session, HashSet<string> keep)
    {
        // Deepest first, then upwards for as long as the folders turn out empty.
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in run.EmptiedFolders.Keys.OrderByDescending(f => f.Length))
        {
            var folder = start;
            while (folder.Length > 0 && !deleted.Contains(folder) && !keep.Contains(folder))
            {
                if (!await session.TryDeleteEmptyFolderAsync(BackupLayout.Current + "/" + folder, run.Token)) break;
                deleted.Add(folder);
                folder = PathText.ParentOf(folder);
            }
        }
    }

    private async Task PruneHistoryAsync(Run run, IDestination session)
    {
        var cutoff = DateTime.Now.AddDays(-run.Job.KeepHistoryDays);
        foreach (var entry in await session.ListAsync(BackupLayout.History, run.Token))
        {
            if (!entry.IsFolder || !BackupLayout.TryParseStamp(entry.Name, out var when) || when >= cutoff) continue;
            await session.DeleteTreeAsync(BackupLayout.History + "/" + entry.Name, run.Token);
            _log.LogInformation("{Backup}: deleted history from {When}, older than {Days} days.", run.Job.Name, entry.Name, run.Job.KeepHistoryDays);
        }
        await _state.SetLastPrunedAsync(run.DestId);
    }

    /// <summary>Hashes the first <paramref name="count"/> bytes of a file. False when the file is shorter.</summary>
    private static async Task<bool> HashPrefixAsync(Stream source, long count, IncrementalHash hash, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        var left = count;
        try
        {
            while (left > 0)
            {
                var n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
                if (n == 0) return false;
                hash.AppendData(buffer, 0, n);
                left -= n;
            }
        }
        catch (IOException ex)
        {
            throw new SourceFileException(ex.Message, ex);
        }
        return true;
    }

    /// <summary>The folder directly under a source that a file belongs to, or the file itself when it sits at the top.</summary>
    private static string TopFolder(BackupJob job, string fullPath)
    {
        var source = job.Sources
            .Where(s => fullPath.Length > s.Length && fullPath.StartsWith(s, BackupJob.PathComparison)
                && (fullPath[s.Length] == Path.DirectorySeparatorChar || s.EndsWith(Path.DirectorySeparatorChar)))
            .OrderByDescending(s => s.Length)
            .FirstOrDefault();
        if (source is null) return fullPath;
        var rest = fullPath[source.Length..].TrimStart(Path.DirectorySeparatorChar);
        var cut = rest.IndexOf(Path.DirectorySeparatorChar);
        return Path.Combine(source, cut < 0 ? rest : rest[..cut]);
    }

    /// <summary>One flat, fixed-length name per file, so <c>partial/</c> needs no folders of its own.</summary>
    internal static string PartialName(string backupPath)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(backupPath)))[..32] + ".part";

    #endregion

    /// <summary>What one run carries around: its counters, its connections, and its stop signal.</summary>
    private sealed class Run(BackupJob job, long destId, long? runId, IDestinationFactory factory, CancellationToken outer) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(outer);
        private readonly ConcurrentBag<IDestination> _idle = [];
        private int _copied, _removed, _failed, _sameContent, _failuresInARow, _live, _limitNoted;
        private long _copiedBytes;

        public BackupJob Job => job;
        public long DestId => destId;
        public long? RunId => runId;
        public CancellationToken Token => _stop.Token;
        public Dictionary<string, FileState> Known { get; set; } = [];
        public string Stamp { get; set; } = "";
        public bool LogEachFile { get; set; }
        public ConcurrentDictionary<string, byte> EmptiedFolders { get; } = new(StringComparer.Ordinal);
        public Exception? AbortReason { get; private set; }
        public string? FirstProblem { get; set; }

        public int Copied => _copied;
        public long CopiedBytes => Interlocked.Read(ref _copiedBytes);
        public int Removed => _removed;
        public int Failed => _failed;
        public int SameContent => _sameContent;

        public void CountCopied(long bytes)
        {
            Interlocked.Increment(ref _copied);
            Interlocked.Add(ref _copiedBytes, bytes);
        }

        public void CountRemoved() => Interlocked.Increment(ref _removed);
        public void CountSameContent() => Interlocked.Increment(ref _sameContent);

        public void CountFailed(string problem)
        {
            Interlocked.Increment(ref _failed);
            FirstProblem ??= problem;
        }

        /// <summary>Something went wrong that is not one file failing.</summary>
        public int Problems { get; private set; }

        public void NoteProblem(string problem)
        {
            Problems++;
            FirstProblem ??= problem;
        }

        public void DestinationWorked() => Interlocked.Exchange(ref _failuresInARow, 0);
        public int DestinationFailed() => Interlocked.Increment(ref _failuresInARow);

        public void Abort(Exception reason)
        {
            AbortReason ??= reason;
            _stop.Cancel();
        }

        /// <summary>Connections that are open now, in use or waiting to be used.</summary>
        public int LiveSessions => _live;

        /// <summary>True the first time it is called, so the limit is logged once.</summary>
        public bool NoteConnectionLimit() => Interlocked.Exchange(ref _limitNoted, 1) == 0;

        public async Task<IDestination> RentAsync()
        {
            if (_idle.TryTake(out var session)) return session;
            session = factory.Open();
            try
            {
                await session.ConnectAsync(Token);
                Interlocked.Increment(ref _live);
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        public void Return(IDestination session) => _idle.Add(session);

        /// <summary>Closes a connection that can't be trusted any more.</summary>
        public async Task DiscardAsync(IDestination session)
        {
            Interlocked.Decrement(ref _live);
            await session.DisposeAsync();
        }

        public async Task WithSessionAsync(Func<IDestination, Task> work)
        {
            var session = await RentAsync();
            try
            {
                await work(session);
            }
            catch
            {
                await DiscardAsync(session);
                throw;
            }
            Return(session);
        }

        /// <summary>Whether a fresh connection can be made and answers.</summary>
        public async Task<bool> DestinationIsReachableAsync()
        {
            try
            {
                await WithSessionAsync(session => session.StatAsync("", Token));
                return true;
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            while (_idle.TryTake(out var session))
                await session.DisposeAsync();
            _stop.Dispose();
        }
    }
}
