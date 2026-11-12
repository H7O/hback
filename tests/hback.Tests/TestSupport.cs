using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HBack.Config;
using HBack.Destinations;
using HBack.Engine;
using HBack.State;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace HBack.Tests;

public static class RunnerExtensions
{
    /// <summary>Runs a backup that has one destination, and gives back how that run went.</summary>
    public static async Task<RunSummary> RunOneAsync(this BackupRunner runner, BackupJob job, bool dryRun = false)
        => Assert.Single(await runner.RunAsync(job, dryRun, CancellationToken.None));

    /// <summary>The same backup, with its one destination changed.</summary>
    public static BackupJob With(this BackupJob job, Func<DestinationSpec, DestinationSpec> change)
        => job with { Destinations = [change(Assert.Single(job.Destinations))] };
}

/// <summary>Hands every logger of a host or a logger factory the one <see cref="TestLogger"/>.</summary>
public sealed class TestLoggerProvider(TestLogger logger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => logger;
    public void Dispose() { }
}

/// <summary>Keeps what hback logs, and shows it in the test output.</summary>
public sealed class TestLogger(ITestOutputHelper output) : ILogger
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var line = $"[{logLevel}] {formatter(state, exception)}";
        Lines.Enqueue(line);
        try
        {
            output.WriteLine(line);
        }
        catch (InvalidOperationException)
        {
            // The test has ended.
        }
    }
}

/// <summary>A scratch folder with a source tree and a state database, for one test.</summary>
public sealed class TestEnv : IAsyncDisposable
{
    public string Root { get; }
    public string SourceDir => Path.Combine(Root, "src");
    public AppPaths Paths { get; }
    public StateStore State { get; private set; } = null!;
    public TestLogger Log { get; }

    private TestEnv(ITestOutputHelper output)
    {
        Root = Path.Combine(Path.GetTempPath(), "hback-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(SourceDir);
        Paths = new AppPaths(Path.Combine(Root, "hback.xml"), Path.Combine(Root, "data"));
        Log = new TestLogger(output);
    }

    public static async Task<TestEnv> CreateAsync(ITestOutputHelper output)
    {
        var env = new TestEnv(output);
        env.State = await StateStore.OpenAsync(env.Paths.DbPath);
        return env;
    }

    /// <summary>Throws away hback's database, as if the machine had been set up again.</summary>
    public async Task LoseStateAsync()
    {
        await State.DisposeAsync();
        foreach (var file in Directory.GetFiles(Paths.DataDir, "hback.db*"))
            File.Delete(file);
        State = await StateStore.OpenAsync(Paths.DbPath);
    }

    public string PathOf(string relative) => Path.Combine(SourceDir, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Where a source file sits inside the backup.</summary>
    public string InBackup(string relative) => PathText.ToBackupPath(PathOf(relative));

    public string Write(string relative, string content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string WriteBytes(string relative, int size, int seed)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public BackupJob Job(DestinationSpec destination, int keepHistoryDays = 30, params string[] excludes) => new()
    {
        Name = "test",
        Sources = [SourceDir],
        Excludes = excludes,
        Destinations = [destination with { KeepHistoryDays = keepHistoryDays, Parallel = 3 }],
    };

    public BackupRunner Runner(Func<DestinationSpec, IDestinationFactory>? factories = null) => new(Paths, State, Log, factories)
    {
        RetryDelays = [TimeSpan.FromMilliseconds(20)],
    };

    public async ValueTask DisposeAsync()
    {
        await State.DisposeAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }
}

/// <summary>
/// Runs portable-sftp (github.com/H7O/portable-sftp) for the SFTP tests, serving a scratch folder on
/// this machine. So a test talks to a real SFTP server and can still look at the files it stored.
/// The server is found through the HBACK_TEST_SFTP_SERVER environment variable (the full path of the
/// portable-sftp program), or on the PATH. Without it the SFTP tests report themselves as skipped,
/// unless HBACK_TEST_SFTP_REQUIRED is set, which makes them fail.
/// </summary>
public sealed class SftpServerFixture : IDisposable
{
    public const string User = "sftp";
    public const string Password = "hback-test-password";

    private readonly Process? _server;
    private readonly string? _scratch;

    public string? Unavailable { get; }
    public string ServedFolder { get; } = "";
    public int Port { get; }

    public SftpServerFixture()
    {
        var program = FindServer();
        if (program is null)
        {
            Unavailable = Missing("No SFTP test server. Set HBACK_TEST_SFTP_SERVER to the path of portable-sftp (github.com/H7O/portable-sftp).");
            return;
        }

        var scratch = _scratch = Path.Combine(Path.GetTempPath(), "hback-tests", "sftp-" + Guid.NewGuid().ToString("N")[..8]);
        ServedFolder = Path.Combine(scratch, "served");
        Directory.CreateDirectory(ServedFolder);
        Port = FreePort();

        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-root", ServedFolder, "-bind", "127.0.0.1", "-port", Port.ToString(), "-user", User, "-hostkey", Path.Combine(scratch, "host_key"), "-max-conns", "32" })
            start.ArgumentList.Add(arg);
        start.Environment["SFTP_PASSWORD"] = Password;

        _server = Process.Start(start)!;
        _server.OutputDataReceived += (_, _) => { };
        _server.ErrorDataReceived += (_, _) => { };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

        if (!WaitForPort(Port, TimeSpan.FromSeconds(10)))
        {
            Dispose();
            Unavailable = Missing($"portable-sftp did not start listening on port {Port}.");
        }
    }

    /// <summary>
    /// A skipped test does not fail a build, so a build machine that lost its SFTP server would pass
    /// with half of the suite not run. Where the SFTP tests have to run, as they do before a
    /// release, HBACK_TEST_SFTP_REQUIRED turns the missing server into an error, and every SFTP test
    /// fails with this reason.
    /// </summary>
    private static string Missing(string why)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HBACK_TEST_SFTP_REQUIRED")))
            throw new InvalidOperationException(why + " HBACK_TEST_SFTP_REQUIRED is set, so this is an error and not a skip.");
        return why;
    }

    public DestinationSpec Destination(string folder) => new()
    {
        IsSftp = true,
        Host = "127.0.0.1",
        Port = Port,
        User = User,
        Password = Password,
        Root = "/" + folder,
    };

    public void Dispose()
    {
        try
        {
            if (_server is { HasExited: false })
            {
                _server.Kill(entireProcessTree: true);
                _server.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // It never started.
        }
        _server?.Dispose();

        try
        {
            if (_scratch is not null) Directory.Delete(_scratch, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the system's own temp clean-up.
        }
    }

    private static string? FindServer()
    {
        var configured = Environment.GetEnvironmentVariable("HBACK_TEST_SFTP_SERVER");
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? configured : null;

        var names = OperatingSystem.IsWindows() ? new[] { "portable-sftp.exe" } : ["portable-sftp"];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(folder => names.Select(name => Path.Combine(folder, name)))
            .FirstOrDefault(File.Exists);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool WaitForPort(int port, TimeSpan patience)
    {
        var until = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                return true;
            }
            catch (SocketException)
            {
                Thread.Sleep(100);
            }
        }
        return false;
    }
}

/// <summary>
/// Puts a real destination behind a switchboard, so a test can make it misbehave on cue: cut a write
/// off after some bytes, or go out of reach altogether.
/// </summary>
public sealed class Troublemaker(IDestinationFactory inner) : IDestinationFactory
{
    private int _writesToCut;

    /// <summary>The next writes are cut off after <see cref="CutAfterBytes"/> bytes, this many times.</summary>
    public int WritesToCut
    {
        get => _writesToCut;
        set => _writesToCut = value;
    }

    public long CutAfterBytes { get; set; }

    /// <summary>When set, nothing connects and nothing can be written.</summary>
    public bool OutOfReach { get; set; }

    /// <summary>After this many completed writes the destination goes out of reach. Null: never.</summary>
    public int? GoOutOfReachAfterWrites { get; set; }

    /// <summary>How many connections may be open at once, as a server with a per-user limit has. Null: no limit.</summary>
    public int? MaxConnections { get; set; }

    public ConcurrentQueue<(string Path, long Offset)> Writes { get; } = new();

    /// <summary>The folders whose contents were asked for.</summary>
    public ConcurrentQueue<string> Lists { get; } = new();

    public int Connections;
    public int MostOpenAtOnce;
    private int _completedWrites;
    private int _open;

    public IDestination Open() => new Session(this, inner.Open());

    private sealed class Session(Troublemaker owner, IDestination real) : IDestination
    {
        private bool _counted;

        public Task ConnectAsync(CancellationToken ct)
        {
            if (owner.OutOfReach) throw new DestinationException("The test destination is out of reach.");
            var open = Interlocked.Increment(ref owner._open);
            if (owner.MaxConnections is { } max && open > max)
            {
                Interlocked.Decrement(ref owner._open);
                throw new DestinationException("The test destination allows no more connections.");
            }
            _counted = true;
            lock (owner) owner.MostOpenAtOnce = Math.Max(owner.MostOpenAtOnce, open);
            Interlocked.Increment(ref owner.Connections);
            return real.ConnectAsync(ct);
        }

        public async Task WriteAsync(string path, Stream content, long offset, CancellationToken ct)
        {
            if (owner.OutOfReach) throw new IOException("The test destination is out of reach.");
            owner.Writes.Enqueue((path, offset));

            if (Interlocked.Decrement(ref owner._writesToCut) >= 0)
            {
                await real.WriteAsync(path, new CutOffStream(content, owner.CutAfterBytes), offset, ct);
                throw new IOException("The test cut the write off, but the stream ended first.");
            }

            await real.WriteAsync(path, content, offset, ct);
            if (owner.GoOutOfReachAfterWrites is { } limit && Interlocked.Increment(ref owner._completedWrites) >= limit)
                owner.OutOfReach = true;
        }

        public Task<DestinationEntry?> StatAsync(string path, CancellationToken ct) => real.StatAsync(path, ct);
        public Task<List<DestinationEntry>> ListAsync(string folder, CancellationToken ct)
        {
            owner.Lists.Enqueue(folder);
            return real.ListAsync(folder, ct);
        }
        public Task CreateFolderAsync(string folder, CancellationToken ct) => real.CreateFolderAsync(folder, ct);
        public Task<long> SealAsync(string path, DateTime modifiedUtc, CancellationToken ct) => real.SealAsync(path, modifiedUtc, ct);
        public Task ReadAsync(string path, Stream output, CancellationToken ct) => real.ReadAsync(path, output, ct);
        public Task MoveAsync(string from, string to, CancellationToken ct) => real.MoveAsync(from, to, ct);
        public Task DeleteFileAsync(string path, CancellationToken ct) => real.DeleteFileAsync(path, ct);
        public Task<bool> TryDeleteEmptyFolderAsync(string folder, CancellationToken ct) => real.TryDeleteEmptyFolderAsync(folder, ct);
        public Task DeleteTreeAsync(string folder, CancellationToken ct) => real.DeleteTreeAsync(folder, ct);

        public ValueTask DisposeAsync()
        {
            if (_counted)
            {
                _counted = false;
                Interlocked.Decrement(ref owner._open);
            }
            return real.DisposeAsync();
        }
    }

    /// <summary>Hands on the first bytes of a stream, then fails as a dropped connection would.</summary>
    private sealed class CutOffStream(Stream inner, long allowed) : Stream
    {
        private long _left = allowed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) throw new IOException("The test cut the connection.");
            var n = inner.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left <= 0) throw new IOException("The test cut the connection.");
            var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], cancellationToken);
            _left -= n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
