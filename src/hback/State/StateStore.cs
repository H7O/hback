using System.Globalization;
using Com.H.Data.Common;
using Microsoft.Data.Sqlite;

namespace HBack.State;

/// <summary>What hback recorded about a file the last time it copied it.</summary>
public sealed record FileState(long Size, long ModifiedTicks, string? Sha256);

public sealed record PartialState(long Size, long ModifiedTicks);

public sealed record RunRecord(
    long Id, string Backup, DateTime StartedUtc, DateTime? FinishedUtc, string Status,
    long Scanned, long Copied, long CopiedBytes, long Removed, long Failed, string? Message);

/// <summary>
/// hback's memory, in a SQLite file: which files are complete at each destination, which large copies
/// were cut off, and how each run went. It is a record of the destination, not the backup itself. If
/// the file is lost, the next run rebuilds it from what it finds at the destination.
/// </summary>
public sealed class StateStore : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    // One connection, used by several copy workers. SQLite wants one statement at a time on it.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private StateStore(SqliteConnection connection) => _connection = connection;

    public static async Task<StateStore> OpenAsync(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();

        var store = new StateStore(connection);
        // WAL lets "hback status" read while a run writes, and makes each small write cheap.
        await store.ExecuteAsync("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 15000;");
        await store.ExecuteAsync(Sql.Schema);
        return store;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _gate.Dispose();
    }

    #region destinations

    public async Task<long> GetDestinationIdAsync(string key)
    {
        await ExecuteAsync(Sql.AddDestination, new { key });
        return (long)(await QueryAsync(Sql.GetDestination, new { key }))[0]["id"]!;
    }

    /// <summary>The id of a destination hback has written to before, or null.</summary>
    public async Task<long?> FindDestinationIdAsync(string key)
        => (await QueryAsync(Sql.GetDestination, new { key })).FirstOrDefault()?["id"] as long?;

    public async Task<DateTime?> GetLastPrunedAsync(string key)
        => ParseTime((await QueryAsync(Sql.GetDestination, new { key })).FirstOrDefault()?["last_pruned"] as string);

    public Task SetLastPrunedAsync(long dest)
        => ExecuteAsync(Sql.SetLastPruned, new { dest, now = Now() });

    #endregion

    #region files

    public async Task<Dictionary<string, FileState>> LoadFilesAsync(long dest)
    {
        var files = new Dictionary<string, FileState>(StringComparer.Ordinal);
        foreach (var row in await QueryAsync(Sql.LoadFiles, new { dest }))
            files[(string)row["path"]!] = new FileState((long)row["size"]!, (long)row["mtime"]!, row["sha256"] as string);
        return files;
    }

    public async Task<(long Files, long Bytes)> CountFilesAsync(long dest)
    {
        var row = (await QueryAsync(Sql.CountFiles, new { dest }))[0];
        return (Convert.ToInt64(row["files"], CultureInfo.InvariantCulture), Convert.ToInt64(row["bytes"], CultureInfo.InvariantCulture));
    }

    public Task UpsertFileAsync(long dest, string path, long size, long mtime, string? sha256, long? run)
        => ExecuteAsync(Sql.UpsertFile, new { dest, path, size, mtime, sha256, now = Now(), run });

    /// <summary>Records a new modified time for a file whose content turned out to be the same.</summary>
    public Task TouchFileAsync(long dest, string path, long mtime)
        => ExecuteAsync(Sql.TouchFile, new { dest, path, mtime });

    public Task DeleteFileAsync(long dest, string path)
        => ExecuteAsync(Sql.DeleteFile, new { dest, path });

    /// <summary>Writes many file records in one transaction.</summary>
    public async Task UpsertFilesAsync(long dest, IEnumerable<(string Path, long Size, long Mtime)> files, long? run)
    {
        await _gate.WaitAsync();
        try
        {
            var now = Now();
            await _connection.ExecuteCommandAsync("BEGIN");
            foreach (var f in files)
                await _connection.ExecuteCommandAsync(Sql.UpsertFile,
                    new { dest, path = f.Path, size = f.Size, mtime = f.Mtime, sha256 = (string?)null, now, run });
            await _connection.ExecuteCommandAsync("COMMIT");
        }
        catch
        {
            try { await _connection.ExecuteCommandAsync("ROLLBACK"); } catch { /* nothing was open */ }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    #endregion

    #region empty folders

    public async Task<HashSet<string>> LoadFoldersAsync(long dest)
        => (await QueryAsync(Sql.LoadFolders, new { dest })).Select(r => (string)r["path"]!).ToHashSet(StringComparer.Ordinal);

    public Task AddFolderAsync(long dest, string path) => ExecuteAsync(Sql.AddFolder, new { dest, path });

    public Task DeleteFolderAsync(long dest, string path) => ExecuteAsync(Sql.DeleteFolder, new { dest, path });

    #endregion

    #region partial copies

    public async Task<PartialState?> GetPartialAsync(long dest, string path)
    {
        var row = (await QueryAsync(Sql.GetPartial, new { dest, path })).FirstOrDefault();
        return row is null ? null : new PartialState((long)row["size"]!, (long)row["mtime"]!);
    }

    public Task SetPartialAsync(long dest, string path, long size, long mtime)
        => ExecuteAsync(Sql.SetPartial, new { dest, path, size, mtime, now = Now() });

    public Task DeletePartialAsync(long dest, string path)
        => ExecuteAsync(Sql.DeletePartial, new { dest, path });

    public Task DeleteAllPartialsAsync(long dest)
        => ExecuteAsync(Sql.DeleteAllPartials, new { dest });

    #endregion

    #region runs

    public async Task<long> StartRunAsync(string backup, long dest)
        => (long)(await QueryAsync(Sql.StartRun, new { backup, dest, now = Now() }))[0]["id"]!;

    public async Task FinishRunAsync(string backup, long run, string status, long scanned, long copied, long copiedBytes, long removed, long failed, string? message)
    {
        await ExecuteAsync(Sql.FinishRun, new { run, now = Now(), status, scanned, copied, copied_bytes = copiedBytes, removed, failed, message });
        if (status == "ok" && copied == 0 && removed == 0 && failed == 0)
            await ExecuteAsync(Sql.DropEarlierIdleRun, new { backup, run });
    }

    public async Task<List<RunRecord>> GetLastRunsAsync(string backup, int take)
        => (await QueryAsync(Sql.LastRun, new { backup, take }))
            .Select(r => new RunRecord(
                (long)r["id"]!, (string)r["backup"]!, ParseTime(r["started_at"] as string) ?? default, ParseTime(r["finished_at"] as string),
                (string)r["status"]!, (long)r["scanned"]!, (long)r["copied"]!, (long)r["copied_bytes"]!, (long)r["removed"]!,
                (long)r["failed"]!, r["message"] as string))
            .ToList();

    public async Task<DateTime?> GetLastGoodRunAsync(string backup)
        => ParseTime((await QueryAsync(Sql.LastGoodRun, new { backup })).FirstOrDefault()?["finished_at"] as string);

    #endregion

    #region host keys

    public async Task<string?> GetHostKeyAsync(string host, int port)
        => (await QueryAsync(Sql.GetHostKey, new { host = host.ToLowerInvariant(), port })).FirstOrDefault()?["fingerprint"] as string;

    public Task AddHostKeyAsync(string host, int port, string fingerprint)
        => ExecuteAsync(Sql.AddHostKey, new { host = host.ToLowerInvariant(), port, fingerprint, now = Now() });

    #endregion

    private async Task ExecuteAsync(string sql, object? parameters = null)
    {
        await _gate.WaitAsync();
        try
        {
            await _connection.ExecuteCommandAsync(sql, parameters);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<IDictionary<string, object?>>> QueryAsync(string sql, object? parameters = null)
    {
        await _gate.WaitAsync();
        try
        {
            var rows = new List<IDictionary<string, object?>>();
            await using var result = await _connection.ExecuteQueryAsync(sql, parameters);
            await foreach (var row in result)
                rows.Add((IDictionary<string, object?>)row);
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Now() => DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

    private static DateTime? ParseTime(string? text)
        => text is not null && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
}
