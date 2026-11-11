using System.Collections.Concurrent;
using System.Net.Sockets;
using Com.H.Net.Ssh;
using HBack.Config;
using HBack.State;
using Renci.SshNet.Common;

namespace HBack.Destinations;

/// <summary>
/// Opens SFTP connections for one backup. The connections share what only needs finding out once:
/// which remote folders are known to exist, and the server's host key.
/// </summary>
public sealed class SftpDestinationFactory(DestinationSpec spec, StateStore state, ILogger log) : IDestinationFactory
{
    internal DestinationSpec Spec => spec;
    internal ILogger Log => log;

    /// <summary>Full remote paths of folders that exist, so each is created or checked once.</summary>
    internal ConcurrentDictionary<string, byte> KnownFolders { get; } = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _hostKeyGate = new(1, 1);
    private string? _trustedHostKey;
    private bool _hostKeyLoaded;

    public IDestination Open() => new SftpDestination(this);

    /// <summary>
    /// The fingerprint the server must show: the one in the configuration, or else the one it showed
    /// the first time hback connected to it. Null when this is that first time.
    /// </summary>
    internal async Task<string?> GetTrustedHostKeyAsync()
    {
        await _hostKeyGate.WaitAsync();
        try
        {
            if (!_hostKeyLoaded)
            {
                _trustedHostKey = Normalize(spec.HostKey) ?? Normalize(await state.GetHostKeyAsync(spec.Host!, spec.Port));
                _hostKeyLoaded = true;
            }
            return _trustedHostKey;
        }
        finally
        {
            _hostKeyGate.Release();
        }
    }

    /// <summary>Remembers the key of a server seen for the first time.</summary>
    internal async Task TrustHostKeyAsync(string fingerprint)
    {
        await _hostKeyGate.WaitAsync();
        try
        {
            if (_trustedHostKey is not null) return;
            _trustedHostKey = fingerprint;
            await state.AddHostKeyAsync(spec.Host!, spec.Port, "SHA256:" + fingerprint);
            log.LogInformation("First connection to {Host}:{Port}. Its host key is SHA256:{Fingerprint}, and hback will expect this key from now on.",
                spec.Host, spec.Port, fingerprint);
        }
        finally
        {
            _hostKeyGate.Release();
        }
    }

    /// <summary>"SHA256:abc=" and "abc" are the same fingerprint.</summary>
    internal static string? Normalize(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        fingerprint = fingerprint.Trim();
        if (fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            fingerprint = fingerprint[7..];
        return fingerprint.TrimEnd('=');
    }
}

/// <summary>
/// A backup kept on an SFTP server, through Com.H.Net.Ssh. The connection stays open for the life of
/// this object.
/// <para>
/// Uploads and folder creation go to the SSH.NET client that Com.H.Net.Ssh exposes, for two reasons.
/// Its own upload checks every folder above the file on every call, which costs several round trips per
/// file where a remembered answer costs none. And it can only write a whole file, where continuing a
/// cut-off copy needs to write from an offset.
/// </para>
/// </summary>
public sealed class SftpDestination(SftpDestinationFactory factory) : IDestination
{
    private readonly DestinationSpec _spec = factory.Spec;
    private SFtpClient? _sftp;
    private string? _rejectedHostKey;

    private Renci.SshNet.SftpClient Client
        => _sftp?.Client ?? throw new InvalidOperationException("The SFTP connection has not been opened.");

    public async Task ConnectAsync(CancellationToken ct)
    {
        var trusted = await factory.GetTrustedHostKeyAsync();
        string? seen = null;

        try
        {
            _sftp = _spec.PrivateKey is not null
                ? new SFtpClient(_spec.Host, _spec.Port, _spec.User, new PrivateKeyFileSettings { Path = _spec.PrivateKey, Passphrase = _spec.Passphrase })
                : new SFtpClient(_spec.Host, _spec.Port, _spec.User, _spec.Password);
            _sftp.KeepConnectionOpen = true;

            // Reading the property builds the SSH.NET client, which is when a private key file is read.
            var client = _sftp.Client;
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
            client.OperationTimeout = TimeSpan.FromMinutes(2);
            client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            client.HostKeyReceived += (_, e) =>
            {
                seen = SftpDestinationFactory.Normalize(e.FingerPrintSHA256);
                e.CanTrust = trusted is null || string.Equals(trusted, seen, StringComparison.Ordinal);
                if (!e.CanTrust) _rejectedHostKey = seen;
            };

            await client.ConnectAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DestinationException(Explain(ex, trusted), ex);
        }

        if (trusted is null && seen is not null)
            await factory.TrustHostKeyAsync(seen);

        await EnsureFolderAsync(Remote(""), ct);
    }

    private string Explain(Exception ex, string? trusted)
    {
        var where = $"{_spec.Host}:{_spec.Port}";
        if (_rejectedHostKey is not null)
            return _spec.HostKey is not null
                ? $"The server at {where} showed the host key SHA256:{_rejectedHostKey}, but <host_key> in the configuration says SHA256:{trusted}. hback did not connect."
                : $"The server at {where} showed a different host key (SHA256:{_rejectedHostKey}) than on hback's first connection to it (SHA256:{trusted}). " +
                  "hback did not connect. If the server was reinstalled and the new key is right, put it in the backup's entry as " +
                  $"<host_key>SHA256:{_rejectedHostKey}</host_key>.";
        return ex switch
        {
            SshAuthenticationException => $"The server at {where} refused the login for user '{_spec.User}': {ex.Message}",
            SocketException or SshOperationTimeoutException or TimeoutException => $"Can't reach {where}: {ex.Message}",
            SshPassPhraseNullOrEmptyException => $"The private key '{_spec.PrivateKey}' needs a <passphrase>.",
            FileNotFoundException or DirectoryNotFoundException => $"The private key file '{_spec.PrivateKey}' was not found.",
            _ => $"Can't connect to {where}: {ex.Message}",
        };
    }

    public async Task<DestinationEntry?> StatAsync(string path, CancellationToken ct)
    {
        try
        {
            var a = await Client.GetAttributesAsync(Remote(path), ct);
            return new DestinationEntry(PathText.NameOf(path), a.IsDirectory, a.Size, a.LastWriteTimeUtc);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    public async Task<List<DestinationEntry>> ListAsync(string folder, CancellationToken ct)
    {
        var list = new List<DestinationEntry>();
        try
        {
            await foreach (var f in Client.ListDirectoryAsync(Remote(folder), ct))
            {
                if (f.Name is "." or "..") continue;
                list.Add(new DestinationEntry(f.Name, f.IsDirectory, f.Length, f.LastWriteTimeUtc));
            }
        }
        catch (SftpPathNotFoundException)
        {
            // No such folder: nothing in it.
        }
        return list;
    }

    public Task CreateFolderAsync(string folder, CancellationToken ct) => EnsureFolderAsync(Remote(folder), ct);

    /// <summary>
    /// Makes sure a remote folder exists. It asks the server to create the folder first and only looks
    /// further when that fails, so a new folder costs one round trip and a remembered one costs none.
    /// </summary>
    private async Task EnsureFolderAsync(string remote, CancellationToken ct)
    {
        if (remote is "" or "." or "/" || factory.KnownFolders.ContainsKey(remote)) return;

        try
        {
            await Client.CreateDirectoryAsync(remote, ct);
        }
        catch (SshException ex) when (ex is not SshConnectionException)
        {
            // It failed because the folder is there already, or because the folder above it isn't.
            if (!await Client.ExistsAsync(remote, ct))
            {
                var parent = RemoteParent(remote);
                if (parent == remote) throw;
                await EnsureFolderAsync(parent, ct);
                try
                {
                    await Client.CreateDirectoryAsync(remote, ct);
                }
                catch (SshException inner) when (inner is not SshConnectionException)
                {
                    // Another connection may have created it in the meantime.
                    if (!await Client.ExistsAsync(remote, ct)) throw;
                }
            }
        }
        factory.KnownFolders[remote] = 0;
    }

    public async Task WriteAsync(string path, Stream content, long offset, CancellationToken ct)
    {
        var remote = Remote(path);
        if (offset == 0)
        {
            await Client.UploadFileAsync(content, remote, ct);
            return;
        }

        await using var file = await Client.OpenAsync(remote, FileMode.Open, FileAccess.Write, ct);
        file.Seek(offset, SeekOrigin.Begin);
        await content.CopyToAsync(file, 128 * 1024, ct);
        await file.FlushAsync(ct);
    }

    public async Task<long> SealAsync(string path, DateTime modifiedUtc, CancellationToken ct)
    {
        var remote = Remote(path);
        var attributes = await Client.GetAttributesAsync(remote, ct);
        var size = attributes.Size;
        try
        {
            // SSH.NET sends only the fields that were changed, so this sets the times and nothing else.
            attributes.LastWriteTimeUtc = modifiedUtc;
            await Task.Run(() => Client.SetAttributes(remote, attributes), ct);
        }
        catch (SshException ex) when (ex is not SshConnectionException)
        {
            // Some servers don't allow it. The file is complete either way, it only shows the upload time.
            factory.Log.LogDebug("The server would not set the modified time of {Path}: {Message}", remote, ex.Message);
        }
        return size;
    }

    public Task ReadAsync(string path, Stream output, CancellationToken ct)
        => Client.DownloadFileAsync(Remote(path), output, ct);

    public Task MoveAsync(string from, string to, CancellationToken ct)
        => _sftp!.RenameAsync(Remote(from), Remote(to), ct);

    public async Task DeleteFileAsync(string path, CancellationToken ct)
    {
        try
        {
            await Client.DeleteFileAsync(Remote(path), ct);
        }
        catch (SftpPathNotFoundException)
        {
            // Already gone.
        }
    }

    public async Task<bool> TryDeleteEmptyFolderAsync(string folder, CancellationToken ct)
    {
        var remote = Remote(folder);
        try
        {
            await Client.DeleteDirectoryAsync(remote, ct);
            factory.KnownFolders.TryRemove(remote, out _);
            return true;
        }
        catch (SshException ex) when (ex is not SshConnectionException)
        {
            return false;
        }
    }

    public async Task DeleteTreeAsync(string folder, CancellationToken ct)
    {
        foreach (var entry in await ListAsync(folder, ct))
        {
            var child = PathText.Join(folder, entry.Name);
            if (entry.IsFolder)
                await DeleteTreeAsync(child, ct);
            else
                await DeleteFileAsync(child, ct);
        }

        var remote = Remote(folder);
        foreach (var known in factory.KnownFolders.Keys.Where(k => k == remote || k.StartsWith(remote + "/", StringComparison.Ordinal)).ToList())
            factory.KnownFolders.TryRemove(known, out _);
        try
        {
            await Client.DeleteDirectoryAsync(remote, ct);
        }
        catch (SftpPathNotFoundException)
        {
            // Already gone.
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _sftp?.Dispose();
        }
        catch
        {
            // The connection may have dropped already.
        }
        _sftp = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>The remote path of a path inside the backup.</summary>
    private string Remote(string path)
    {
        var root = _spec.Root;
        if (path.Length == 0) return root;
        return root switch
        {
            "." => path,
            "/" => "/" + path,
            _ => root + "/" + path,
        };
    }

    private static string RemoteParent(string remote)
    {
        var i = remote.LastIndexOf('/');
        return i < 0 ? "" : i == 0 ? "/" : remote[..i];
    }
}
