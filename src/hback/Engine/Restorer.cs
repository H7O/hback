using HBack.Config;
using HBack.Destinations;
using HBack.State;

namespace HBack.Engine;

public sealed record RestoreResult(int Restored, long Bytes, int AlreadyThere, int Failed);

/// <summary>
/// Copies files back from a backup into a folder of your choice. It never writes over the original
/// location by itself: the files land under the folder you name, in the same layout as the backup.
/// </summary>
public sealed class Restorer(StateStore state, ILogger log)
{
    /// <param name="path">A folder or file inside the backup ("C/code/app"), or the original path
    /// ("C:\code\app"). Null restores everything.</param>
    /// <param name="historyFolder">The name of a folder under <c>history/</c>, to restore the copies
    /// that were set aside by that run. Null restores the current files.</param>
    public async Task<RestoreResult> RestoreAsync(BackupJob job, string? path, string? historyFolder, string targetFolder, CancellationToken ct)
    {
        targetFolder = Path.GetFullPath(targetFolder);
        Directory.CreateDirectory(targetFolder);

        var from = historyFolder is null ? BackupLayout.Current : BackupLayout.History + "/" + historyFolder;
        var start = string.IsNullOrWhiteSpace(path) ? "" : ToBackupPath(path);

        await using var session = DestinationFactory.For(job.Destination, state, log).Open();
        await session.ConnectAsync(ct);

        var first = await session.StatAsync(PathText.Join(from, start), ct)
            ?? throw new DestinationException($"There is no '{PathText.Join(from, start)}' in the backup at {job.Destination.Display}.");

        int restored = 0, alreadyThere = 0, failed = 0;
        long bytes = 0;

        async Task RestoreFileAsync(string backupPath, DestinationEntry entry)
        {
            var target = Path.GetFullPath(Path.Combine(targetFolder, backupPath.Replace('/', Path.DirectorySeparatorChar)));
            // The names come from the server. A name must never lead outside the folder that was asked for.
            if (!target.StartsWith(targetFolder + Path.DirectorySeparatorChar, BackupJob.PathComparison))
            {
                failed++;
                log.LogError("Skipped '{Path}': its name leads outside the restore folder.", backupPath);
                return;
            }

            var existing = new FileInfo(target);
            if (existing.Exists && existing.Length == entry.Size && Math.Abs((existing.LastWriteTimeUtc - entry.ModifiedUtc).TotalSeconds) <= 2)
            {
                alreadyThere++;
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var incoming = target + ".hback-part";
                await using (var file = new FileStream(incoming, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                    await session.ReadAsync(PathText.Join(from, backupPath), file, ct);
                File.SetLastWriteTimeUtc(incoming, entry.ModifiedUtc);
                File.Move(incoming, target, overwrite: true);
                restored++;
                bytes += entry.Size;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                log.LogError("Could not restore '{Path}': {Message}", backupPath, ex.Message);
            }
        }

        if (!first.IsFolder)
        {
            await RestoreFileAsync(start, first);
            return new RestoreResult(restored, bytes, alreadyThere, failed);
        }

        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            var entries = await session.ListAsync(PathText.Join(from, folder), ct);
            foreach (var entry in entries)
            {
                var child = PathText.Join(folder, entry.Name);
                if (entry.IsFolder) pending.Push(child);
                else await RestoreFileAsync(child, entry);
            }

            // An empty folder is part of the tree too.
            if (entries.Count == 0 && folder.Length > 0)
            {
                var target = Path.GetFullPath(Path.Combine(targetFolder, folder.Replace('/', Path.DirectorySeparatorChar)));
                if (target.StartsWith(targetFolder + Path.DirectorySeparatorChar, BackupJob.PathComparison))
                    Directory.CreateDirectory(target);
            }
        }
        return new RestoreResult(restored, bytes, alreadyThere, failed);
    }

    private static string ToBackupPath(string path)
    {
        path = path.Trim();
        return Path.IsPathFullyQualified(path)
            ? PathText.ToBackupPath(PathText.TrimEndSeparators(Path.GetFullPath(path)))
            : path.Replace('\\', '/').Trim('/');
    }
}
