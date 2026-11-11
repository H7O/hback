using HBack.Config;

namespace HBack.Destinations;

public sealed class LocalDestinationFactory(DestinationSpec spec) : IDestinationFactory
{
    public IDestination Open() => new LocalDestination(spec.Root);
}

/// <summary>A backup kept in a folder: another disk, a USB drive, a network share.</summary>
public sealed class LocalDestination(string root) : IDestination
{
    public Task ConnectAsync(CancellationToken ct)
    {
        // hback doesn't create this folder. If it did, an unplugged drive or an unmounted share would
        // look like an empty destination, and the backup would quietly land on the wrong disk.
        if (!Directory.Exists(root))
            throw new DestinationException(
                $"The destination folder '{root}' doesn't exist. Create it if that is the right place. " +
                "hback doesn't create it, so that an unplugged or unmounted drive is never mistaken for an empty one.");
        return Task.CompletedTask;
    }

    public Task<DestinationEntry?> StatAsync(string path, CancellationToken ct)
    {
        var full = Full(path);
        var file = new FileInfo(full);
        if (file.Exists)
            return Task.FromResult<DestinationEntry?>(new DestinationEntry(file.Name, false, file.Length, file.LastWriteTimeUtc));
        var folder = new DirectoryInfo(full);
        return Task.FromResult(folder.Exists ? new DestinationEntry(folder.Name, true, 0, folder.LastWriteTimeUtc) : null);
    }

    public Task<List<DestinationEntry>> ListAsync(string folder, CancellationToken ct)
    {
        var list = new List<DestinationEntry>();
        var info = new DirectoryInfo(Full(folder));
        if (info.Exists)
            foreach (var e in info.EnumerateFileSystemInfos())
                list.Add(e is FileInfo f
                    ? new DestinationEntry(f.Name, false, f.Length, f.LastWriteTimeUtc)
                    : new DestinationEntry(e.Name, true, 0, e.LastWriteTimeUtc));
        return Task.FromResult(list);
    }

    public Task CreateFolderAsync(string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(Full(folder));
        return Task.CompletedTask;
    }

    public async Task WriteAsync(string path, Stream content, long offset, CancellationToken ct)
    {
        await using var file = new FileStream(Full(path), offset == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write,
            FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offset > 0)
        {
            file.SetLength(offset);
            file.Position = offset;
        }
        await content.CopyToAsync(file, 128 * 1024, ct);
    }

    public Task<long> SealAsync(string path, DateTime modifiedUtc, CancellationToken ct)
    {
        var full = Full(path);
        File.SetLastWriteTimeUtc(full, modifiedUtc);
        return Task.FromResult(new FileInfo(full).Length);
    }

    public async Task ReadAsync(string path, Stream output, CancellationToken ct)
    {
        await using var file = new FileStream(Full(path), FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await file.CopyToAsync(output, 128 * 1024, ct);
    }

    public Task MoveAsync(string from, string to, CancellationToken ct)
    {
        File.Move(Full(from), Full(to), overwrite: false);
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string path, CancellationToken ct)
    {
        var full = Full(path);
        try
        {
            File.Delete(full);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone, with its folder.
        }
        catch (UnauthorizedAccessException) when (File.Exists(full))
        {
            // A read-only file. It was copied that way from the source.
            File.SetAttributes(full, FileAttributes.Normal);
            File.Delete(full);
        }
        return Task.CompletedTask;
    }

    public Task<bool> TryDeleteEmptyFolderAsync(string folder, CancellationToken ct)
    {
        try
        {
            Directory.Delete(Full(folder), recursive: false);
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
    }

    public Task DeleteTreeAsync(string folder, CancellationToken ct)
    {
        var info = new DirectoryInfo(Full(folder));
        if (!info.Exists) return Task.CompletedTask;
        // Directory.Delete stops at a read-only file, so those are cleared first.
        foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                file.Attributes = FileAttributes.Normal;
        }
        info.Delete(recursive: true);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string Full(string path)
        => path.Length == 0 ? root : Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
}
