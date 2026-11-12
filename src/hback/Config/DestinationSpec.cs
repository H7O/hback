using System.Text.RegularExpressions;
using HBack.Engine;

namespace HBack.Config;

/// <summary>How much of a source's own path is repeated at the destination.</summary>
public enum SourcePathStyle
{
    /// <summary>All of it: "C:\code\app\a.cs" is stored as "C/code/app/a.cs". No two sources can collide.</summary>
    Full,

    /// <summary>The source folder's own name: "C:\code\app\a.cs" from the source "C:\code\app" is stored as "app/a.cs".</summary>
    Name,

    /// <summary>None of it: the same file is stored as "a.cs". The contents of the source go straight in.</summary>
    None,
}

/// <summary>
/// One place a backup goes: a local folder, or a folder on an SFTP server. It also says how the
/// files are laid out there and how that place is treated, because two destinations of one backup
/// may differ in all of that.
/// </summary>
public sealed partial record DestinationSpec
{
    public bool IsSftp { get; init; }

    /// <summary>
    /// The folder the backup lives in. Local: a full path. SFTP: a remote path, absolute
    /// ("/backups/laptop") or relative to the login folder ("backups/laptop", "." for the login folder).
    /// </summary>
    public required string Root { get; init; }

    public string? Host { get; init; }
    public int Port { get; init; } = 22;
    public string? User { get; init; }
    public string? Password { get; init; }
    public string? PrivateKey { get; init; }
    public string? Passphrase { get; init; }

    /// <summary>The server's host key fingerprint ("SHA256:..."), when the configuration pins one.</summary>
    public string? HostKey { get; init; }

    /// <summary>A short name for this destination, for logs and for <c>--destination</c>. Optional.</summary>
    public string? Name { get; init; }

    public SourcePathStyle SourcePath { get; init; } = SourcePathStyle.Full;

    /// <summary>
    /// The folder, inside <see cref="Root"/>, that holds the files as they are now. Written with
    /// forward slashes. Empty means the destination folder itself.
    /// </summary>
    public string CurrentFolder { get; init; } = BackupLayout.Current;

    /// <summary>The folder, inside <see cref="Root"/>, for the previous copies of changed and deleted files.</summary>
    public string HistoryFolder { get; init; } = BackupLayout.History;

    /// <summary>The folder, inside <see cref="Root"/>, for files on their way in.</summary>
    public string PartialFolder { get; init; } = BackupLayout.Partial;

    /// <summary>
    /// How long the previous copy of a changed or deleted file is kept here.
    /// Zero keeps nothing: a changed file is overwritten and a deleted file is removed.
    /// </summary>
    public int KeepHistoryDays { get; init; } = 30;

    /// <summary>How many files are copied at the same time. Each one uses its own SFTP connection.</summary>
    public int Parallel { get; init; } = 4;

    /// <summary>The server and folder, without the layout inside it. It holds no secrets.</summary>
    public string Place => IsSftp ? $"sftp://{User}@{Host!.ToLowerInvariant()}:{Port}{RootInUrl}" : Root;

    /// <summary>
    /// Names the destination in the state database. Two backups with the same id share one record of
    /// what is stored there. The folder the files are in is part of it, so when that folder is changed
    /// hback takes stock of the new one and does not take the old one's record for it.
    /// </summary>
    public string Id => CurrentFolder == BackupLayout.Current ? Place : $"{Place} [{(CurrentFolder.Length == 0 ? "." : CurrentFolder)}]";

    /// <summary>What a person reads in logs and in the status table.</summary>
    public string Display => IsSftp ? $"sftp://{User}@{Host}:{Port}{RootInUrl}" : Root;

    /// <summary>What this destination is called where a backup has several: its name, or else its address.</summary>
    public string Label => Name ?? Display;

    private string RootInUrl => Root.StartsWith('/') ? Root : Root == "." ? "/~" : "/~/" + Root;

    /// <summary>The path, inside <see cref="Root"/>, of a file that is stored at <paramref name="path"/>.</summary>
    public string InCurrent(string path) => PathText.Join(CurrentFolder, path);

    /// <summary>The same for the copy one run set aside. <paramref name="run"/> is that run's folder name.</summary>
    public string InHistory(string run, string path = "") => PathText.Join(PathText.Join(HistoryFolder, run), path);

    public string InPartial(string name) => PathText.Join(PartialFolder, name);

    /// <summary>
    /// hback's own folders that sit inside <see cref="CurrentFolder"/>, as paths within it. There are
    /// none in the usual layout, where the three folders are side by side. When the files go straight
    /// into the destination folder, history and partial are in there with them, and everything that
    /// walks the files has to step around these.
    /// </summary>
    public IReadOnlyList<string> OwnFoldersInCurrent
    {
        get
        {
            var inside = new List<string>(2);
            foreach (var folder in (string[])[HistoryFolder, PartialFolder])
                if (folder.Length > CurrentFolder.Length && PathText.IsSameOrInside(folder, CurrentFolder))
                    inside.Add(CurrentFolder.Length == 0 ? folder : folder[(CurrentFolder.Length + 1)..]);
            return inside;
        }
    }

    /// <summary>
    /// Whether this destination and another one write into the same folders, when both are in the same
    /// place. Each would then take the other's files for its own.
    /// </summary>
    public bool SharesFoldersWith(DestinationSpec other)
    {
        if (IsSftp != other.IsSftp
            || !string.Equals(Place, other.Place, IsSftp ? StringComparison.Ordinal : BackupJob.PathComparison))
            return false;

        string[] mine = [CurrentFolder, HistoryFolder, PartialFolder];
        string[] theirs = [other.CurrentFolder, other.HistoryFolder, other.PartialFolder];
        return mine.Any(a => theirs.Any(b => PathText.IsSameOrInside(a, b) || PathText.IsSameOrInside(b, a)));
    }

    [GeneratedRegex(@"^sftp://(?:(?<user>[^:@/]+)(?::(?<pwd>[^@/]*))?@)?(?<host>\[[^\]]+\]|[^:/@]+)(?::(?<port>\d+))?(?<path>/.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SftpUrl();

    /// <param name="destination">"sftp://user@host:22/folder", or a local folder.</param>
    /// <param name="get">Reads the other settings of the destination by tag name.</param>
    public static DestinationSpec Parse(string destination, Func<string, string?> get)
    {
        destination = destination.Trim();
        string? Setting(string tag)
        {
            var value = get(tag)?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        return WithSettings(ParseAddress(destination, Setting), Setting);
    }

    private static DestinationSpec ParseAddress(string destination, Func<string, string?> get)
    {
        if (!destination.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase))
        {
            var local = PathText.Expand(destination);
            if (!Path.IsPathFullyQualified(local))
                throw new ConfigException($"The destination '{destination}' must be a full path, or start with sftp://.");
            return new DestinationSpec { Root = PathText.TrimEndSeparators(Path.GetFullPath(local)) };
        }

        var m = SftpUrl().Match(destination);
        if (!m.Success)
            throw new ConfigException($"The destination '{destination}' isn't a valid SFTP address. Write it as sftp://user@host:22/folder.");

        var port = 22;
        if (m.Groups["port"].Success && (!int.TryParse(m.Groups["port"].Value, out port) || port is < 1 or > 65535))
            throw new ConfigException($"The destination '{destination}' has an invalid port.");

        // "/folder" is absolute on the server. "/~/folder" is under the login folder, as is no path at all.
        var path = m.Groups["path"].Success ? Uri.UnescapeDataString(m.Groups["path"].Value) : "/~";
        string root;
        if (path == "/~" || path.StartsWith("/~/", StringComparison.Ordinal))
        {
            root = path.Length > 3 ? path[3..].Trim('/') : "";
            if (root.Length == 0) root = ".";
        }
        else
        {
            root = "/" + path.Trim('/');
        }

        var privateKey = get("private_key");
        var password = get("password") ?? (m.Groups["pwd"].Success ? Uri.UnescapeDataString(m.Groups["pwd"].Value) : null);
        if (string.IsNullOrEmpty(privateKey) && string.IsNullOrEmpty(password))
            throw new ConfigException($"The destination '{destination}' needs a <private_key> or a <password>.");

        return new DestinationSpec
        {
            IsSftp = true,
            Root = root,
            Host = m.Groups["host"].Value.Trim('[', ']'),
            Port = port,
            User = m.Groups["user"].Success ? Uri.UnescapeDataString(m.Groups["user"].Value) : Environment.UserName,
            Password = string.IsNullOrEmpty(privateKey) ? password : null,
            PrivateKey = string.IsNullOrEmpty(privateKey) ? null : Path.GetFullPath(PathText.Expand(privateKey)),
            Passphrase = get("passphrase"),
            HostKey = get("host_key"),
        };
    }

    /// <summary>The settings that are the same for a folder and for a server: the layout, and how the place is treated.</summary>
    private static DestinationSpec WithSettings(DestinationSpec spec, Func<string, string?> get)
    {
        var style = get("source_path")?.ToLowerInvariant() switch
        {
            null or "full" => SourcePathStyle.Full,
            "name" => SourcePathStyle.Name,
            "none" => SourcePathStyle.None,
            _ => throw new ConfigException($"<source_path> is '{get("source_path")}'. It can be full, name or none."),
        };

        var current = Folder(get("current_folder"), "current_folder", mayBeTheDestination: true) ?? BackupLayout.Current;
        // Files that go straight into the destination folder should not have two folders of hback's
        // dropped among them under plain names, so these move into one folder that is clearly hback's.
        var beside = current.Length == 0 ? ".hback/" : "";
        var history = Folder(get("history_folder"), "history_folder", mayBeTheDestination: false) ?? beside + BackupLayout.History;
        var partial = Folder(get("partial_folder"), "partial_folder", mayBeTheDestination: false) ?? beside + BackupLayout.Partial;

        if (PathText.IsSameOrInside(history, partial) || PathText.IsSameOrInside(partial, history))
            throw new ConfigException($"<history_folder> ({history}) and <partial_folder> ({partial}) must be two separate folders, neither inside the other.");
        foreach (var (own, tag) in new[] { (history, "history_folder"), (partial, "partial_folder") })
            if (current.Length > 0 && PathText.IsSameOrInside(current, own))
                throw new ConfigException($"<current_folder> ({current}) can't be the same folder as <{tag}> ({own}), or inside it.");

        return spec with
        {
            SourcePath = style,
            CurrentFolder = current,
            HistoryFolder = history,
            PartialFolder = partial,
            KeepHistoryDays = Number(get("keep_history_days"), 30, 0, 36500, "keep_history_days"),
            Parallel = Number(get("parallel"), 4, 1, 16, "parallel"),
        };
    }

    /// <summary>
    /// A folder inside the destination, as hback uses it: forward slashes, no slash at either end.
    /// Null when the setting isn't there. It has to stay inside the destination, so that everything
    /// hback writes is under the one folder the configuration names, and a file can always be moved
    /// between the three folders without being copied.
    /// </summary>
    private static string? Folder(string? text, string tag, bool mayBeTheDestination)
    {
        if (text is null) return null;
        var path = text.Replace('\\', '/');
        if (path is "." or "./")
            return mayBeTheDestination
                ? ""
                : throw new ConfigException($"<{tag}> can't be the destination folder itself. hback needs a folder of its own for it.");

        var parts = path.TrimEnd('/').Split('/');
        if (path.StartsWith('/') || Path.IsPathRooted(text) || parts.Any(p => p.Length == 0 || p is "." or ".." || p != p.Trim()))
            throw new ConfigException($"<{tag}> is '{text}'. It must be a folder inside the destination, such as 'files' or 'kept/history'" +
                (mayBeTheDestination ? ", or '.' for the destination folder itself." : "."));
        return string.Join('/', parts);
    }

    private static int Number(string? text, int fallback, int min, int max, string tag)
    {
        if (text is null) return fallback;
        if (!int.TryParse(text, out var n) || n < min || n > max)
            throw new ConfigException($"<{tag}> must be a number from {min} to {max}.");
        return n;
    }
}
