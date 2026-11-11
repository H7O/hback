namespace HBack.State;

/// <summary>
/// Every SQL statement hback runs, in one place. Values are written as {{name}} and sent as real
/// parameters by Com.H.Data.Common.
/// </summary>
internal static class Sql
{
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS destinations (
            id   INTEGER PRIMARY KEY AUTOINCREMENT,
            key  TEXT NOT NULL UNIQUE,
            last_pruned TEXT
        );

        -- One row per file that is complete at a destination, with the size and time the source
        -- file had when it was copied. A file is copied again when either differs.
        CREATE TABLE IF NOT EXISTS files (
            dest_id      INTEGER NOT NULL,
            path         TEXT    NOT NULL,
            size         INTEGER NOT NULL,
            mtime        INTEGER NOT NULL,
            sha256       TEXT,
            backed_up_at TEXT    NOT NULL,
            run_id       INTEGER,
            PRIMARY KEY (dest_id, path)
        ) WITHOUT ROWID;

        -- Folders that are empty at the source. A backup of files alone would lose them, and some
        -- tools need them: git does not accept a repository without its refs folder.
        CREATE TABLE IF NOT EXISTS folders (
            dest_id INTEGER NOT NULL,
            path    TEXT    NOT NULL,
            PRIMARY KEY (dest_id, path)
        ) WITHOUT ROWID;

        -- A large file whose copy has started. If the run is cut off, the next one continues the
        -- same copy as long as the source still has this size and time.
        CREATE TABLE IF NOT EXISTS partials (
            dest_id    INTEGER NOT NULL,
            path       TEXT    NOT NULL,
            size       INTEGER NOT NULL,
            mtime      INTEGER NOT NULL,
            started_at TEXT    NOT NULL,
            PRIMARY KEY (dest_id, path)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS runs (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            backup       TEXT    NOT NULL,
            dest_id      INTEGER NOT NULL,
            started_at   TEXT    NOT NULL,
            finished_at  TEXT,
            status       TEXT    NOT NULL,
            scanned      INTEGER NOT NULL DEFAULT 0,
            copied       INTEGER NOT NULL DEFAULT 0,
            copied_bytes INTEGER NOT NULL DEFAULT 0,
            removed      INTEGER NOT NULL DEFAULT 0,
            failed       INTEGER NOT NULL DEFAULT 0,
            message      TEXT
        );
        CREATE INDEX IF NOT EXISTS runs_by_backup ON runs (backup, id);

        -- The host key each SFTP server showed the first time hback connected to it.
        CREATE TABLE IF NOT EXISTS known_hosts (
            host        TEXT    NOT NULL,
            port        INTEGER NOT NULL,
            fingerprint TEXT    NOT NULL,
            first_seen  TEXT    NOT NULL,
            PRIMARY KEY (host, port)
        ) WITHOUT ROWID;
        """;

    public const string AddDestination = "INSERT OR IGNORE INTO destinations (key) VALUES ({{key}})";
    public const string GetDestination = "SELECT id, last_pruned FROM destinations WHERE key = {{key}}";
    public const string SetLastPruned = "UPDATE destinations SET last_pruned = {{now}} WHERE id = {{dest}}";

    public const string LoadFiles = "SELECT path, size, mtime, sha256 FROM files WHERE dest_id = {{dest}}";
    public const string CountFiles = "SELECT COUNT(*) AS files, COALESCE(SUM(size), 0) AS bytes FROM files WHERE dest_id = {{dest}}";

    public const string UpsertFile = """
        INSERT INTO files (dest_id, path, size, mtime, sha256, backed_up_at, run_id)
        VALUES ({{dest}}, {{path}}, {{size}}, {{mtime}}, {{sha256}}, {{now}}, {{run}})
        ON CONFLICT (dest_id, path) DO UPDATE SET
            size = excluded.size, mtime = excluded.mtime, sha256 = excluded.sha256,
            backed_up_at = excluded.backed_up_at, run_id = excluded.run_id
        """;

    public const string TouchFile = "UPDATE files SET mtime = {{mtime}} WHERE dest_id = {{dest}} AND path = {{path}}";
    public const string DeleteFile = "DELETE FROM files WHERE dest_id = {{dest}} AND path = {{path}}";

    public const string LoadFolders = "SELECT path FROM folders WHERE dest_id = {{dest}}";
    public const string AddFolder = "INSERT OR IGNORE INTO folders (dest_id, path) VALUES ({{dest}}, {{path}})";
    public const string DeleteFolder = "DELETE FROM folders WHERE dest_id = {{dest}} AND path = {{path}}";

    public const string GetPartial ="SELECT size, mtime FROM partials WHERE dest_id = {{dest}} AND path = {{path}}";
    public const string SetPartial = """
        INSERT INTO partials (dest_id, path, size, mtime, started_at)
        VALUES ({{dest}}, {{path}}, {{size}}, {{mtime}}, {{now}})
        ON CONFLICT (dest_id, path) DO UPDATE SET
            size = excluded.size, mtime = excluded.mtime, started_at = excluded.started_at
        """;
    public const string DeletePartial = "DELETE FROM partials WHERE dest_id = {{dest}} AND path = {{path}}";
    public const string DeleteAllPartials = "DELETE FROM partials WHERE dest_id = {{dest}}";

    public const string StartRun = """
        INSERT INTO runs (backup, dest_id, started_at, status)
        VALUES ({{backup}}, {{dest}}, {{now}}, 'running')
        RETURNING id
        """;

    public const string FinishRun = """
        UPDATE runs SET
            finished_at = {{now}}, status = {{status}}, scanned = {{scanned}}, copied = {{copied}},
            copied_bytes = {{copied_bytes}}, removed = {{removed}}, failed = {{failed}}, message = {{message}}
        WHERE id = {{run}}
        """;

    // A backup that runs every few minutes mostly finds nothing to do. Those runs would bury the
    // ones that did something, so a run with nothing to do replaces the one before it when that
    // one had nothing to do either. The newest is kept, which is what "last run" should show.
    public const string DropEarlierIdleRun = """
        DELETE FROM runs
        WHERE id = (SELECT MAX(id) FROM runs WHERE backup = {{backup}} AND id < {{run}})
          AND status = 'ok' AND copied = 0 AND removed = 0 AND failed = 0
        """;

    public const string LastRun = """
        SELECT id, backup, dest_id, started_at, finished_at, status, scanned, copied, copied_bytes, removed, failed, message
        FROM runs WHERE backup = {{backup}} ORDER BY id DESC LIMIT {{take}}
        """;

    public const string LastGoodRun = """
        SELECT finished_at FROM runs
        WHERE backup = {{backup}} AND status = 'ok' ORDER BY id DESC LIMIT 1
        """;

    public const string GetHostKey = "SELECT fingerprint FROM known_hosts WHERE host = {{host}} AND port = {{port}}";
    public const string AddHostKey = """
        INSERT OR IGNORE INTO known_hosts (host, port, fingerprint, first_seen)
        VALUES ({{host}}, {{port}}, {{fingerprint}}, {{now}})
        """;
}
