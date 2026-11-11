using HBack.Config;

namespace HBack.Scan;

/// <summary>A file found under a source, with the size and time it had when it was found.</summary>
/// <param name="FullPath">Where it is on this machine.</param>
/// <param name="BackupPath">Where it goes inside the backup. See <see cref="PathText.ToBackupPath"/>.</param>
/// <param name="ModifiedTicks">Last write time in UTC ticks.</param>
public sealed record SourceFile(string FullPath, string BackupPath, long Size, long ModifiedTicks);

public sealed class ScanResult
{
    public List<SourceFile> Files { get; } = [];

    /// <summary>
    /// Backup paths of the folders with nothing in them to back up. They are created at the
    /// destination as empty folders, so a restore gives back the same tree.
    /// </summary>
    public List<string> EmptyFolders { get; } = [];

    /// <summary>What could not be read, worded for the log.</summary>
    public List<string> Problems { get; } = [];

    public int Excluded { get; set; }
    public int Links { get; set; }

    private readonly List<string> _unreadable = [];

    /// <summary>
    /// Records that a source or a folder could not be read. Files hback knows from an earlier run and
    /// that sit under it are then left alone: "I could not look" must never be taken as "it was deleted".
    /// </summary>
    public void MarkUnreadable(string backupPath, string problem)
    {
        _unreadable.Add(backupPath);
        Problems.Add(problem);
    }

    public bool IsUnreadable(string backupPath)
    {
        foreach (var p in _unreadable)
        {
            if (p.Length == 0 || backupPath.Equals(p, StringComparison.Ordinal)) return true;
            if (backupPath.Length > p.Length && backupPath[p.Length] == '/' && backupPath.StartsWith(p, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Walks the sources of a backup and lists the files to keep. It never follows a link (a symbolic
/// link or a junction), so it can't loop and can't wander out of the sources.
/// </summary>
public sealed class SourceScanner
{
    private static readonly EnumerationOptions Everything = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <param name="skipFolders">Folders to stay out of whatever the patterns say: hback's own data
    /// folder, and the destination when it is a local folder inside a source.</param>
    public ScanResult Scan(BackupJob job, IEnumerable<string> skipFolders, CancellationToken ct)
    {
        var result = new ScanResult();
        var matcher = new ExcludeMatcher(job.Excludes, ignoreCase: !OperatingSystem.IsLinux());
        var skip = new HashSet<string>(skipFolders.Select(PathText.TrimEndSeparators), BackupJob.PathComparer);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in job.Sources)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(source))
            {
                // A file named as a source is wanted, so the exclude patterns don't apply to it.
                try
                {
                    Add(new FileInfo(source), PathText.ToBackupPath(source), result, seen);
                }
                catch (Exception ex) when (IsReadProblem(ex))
                {
                    result.MarkUnreadable(PathText.ToBackupPath(source), $"Can't read '{source}': {ex.Message}");
                }
            }
            else if (Directory.Exists(source))
            {
                Walk(source, matcher, skip, result, seen, ct);
            }
            else
            {
                result.MarkUnreadable(PathText.ToBackupPath(source), $"The source '{source}' was not found. Its files in the backup are left as they are.");
            }
        }
        return result;
    }

    private static void Walk(string root, ExcludeMatcher matcher, HashSet<string> skip, ScanResult result,
        HashSet<string> seen, CancellationToken ct)
    {
        // (folder, its path from the top of the source)
        var pending = new Stack<(string Folder, string Relative)>();
        pending.Push((root, ""));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (folder, relative) = pending.Pop();
            if (skip.Contains(folder)) continue;

            var folderBackupPath = PathText.ToBackupPath(folder);
            var folderMatchPath = PathText.ToMatchPath(folder);
            try
            {
                var kept = 0;
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Everything))
                {
                    var attributes = entry.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0 && IsLink(entry))
                    {
                        result.Links++;
                        continue;
                    }

                    var isFolder = (attributes & FileAttributes.Directory) != 0;
                    var entryRelative = PathText.Join(relative, entry.Name);
                    if (matcher.IsExcluded(entry.Name, entryRelative, PathText.Join(folderMatchPath, entry.Name), isFolder))
                    {
                        result.Excluded++;
                        continue;
                    }

                    kept++;
                    if (isFolder)
                        pending.Push((entry.FullName, entryRelative));
                    else
                        Add((FileInfo)entry, PathText.Join(folderBackupPath, entry.Name), result, seen);
                }

                if (kept == 0 && seen.Add(folderBackupPath + "/"))
                    result.EmptyFolders.Add(folderBackupPath);
            }
            catch (Exception ex) when (IsReadProblem(ex))
            {
                result.MarkUnreadable(folderBackupPath, $"Can't read the folder '{folder}': {ex.Message}");
            }
        }
    }

    private static void Add(FileInfo file, string backupPath, ScanResult result, HashSet<string> seen)
    {
        // Two sources can overlap ("C:\code" and "C:\code\app"). The file is listed once.
        if (seen.Add(backupPath))
            result.Files.Add(new SourceFile(file.FullName, backupPath, file.Length, file.LastWriteTimeUtc.Ticks));
    }

    private static bool IsLink(FileSystemInfo entry)
    {
        try
        {
            // Not every reparse point is a link: a file kept in cloud storage has one too, and is a real file.
            return entry.LinkTarget is not null;
        }
        catch (Exception ex) when (IsReadProblem(ex))
        {
            return true;
        }
    }

    private static bool IsReadProblem(Exception ex)
        => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
