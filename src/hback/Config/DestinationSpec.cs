using System.Text.RegularExpressions;

namespace HBack.Config;

/// <summary>
/// Where a backup goes: a local folder, or a folder on an SFTP server.
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

    /// <summary>
    /// Names the destination in the state database. It holds no secrets, and two backups with the same
    /// id share one record of what is stored there.
    /// </summary>
    public string Id => IsSftp ? $"sftp://{User}@{Host!.ToLowerInvariant()}:{Port}{RootInUrl}" : Root;

    /// <summary>What a person reads in logs and in the status table.</summary>
    public string Display => IsSftp ? $"sftp://{User}@{Host}:{Port}{RootInUrl}" : Root;

    private string RootInUrl => Root.StartsWith('/') ? Root : Root == "." ? "/~" : "/~/" + Root;

    [GeneratedRegex(@"^sftp://(?:(?<user>[^:@/]+)(?::(?<pwd>[^@/]*))?@)?(?<host>\[[^\]]+\]|[^:/@]+)(?::(?<port>\d+))?(?<path>/.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SftpUrl();

    /// <param name="destination">"sftp://user@host:22/folder", or a local folder.</param>
    /// <param name="get">Reads the other settings of the backup by tag name.</param>
    public static DestinationSpec Parse(string destination, Func<string, string?> get)
    {
        destination = destination.Trim();
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
}
