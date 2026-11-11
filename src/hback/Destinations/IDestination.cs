using HBack.Config;
using HBack.State;

namespace HBack.Destinations;

public sealed record DestinationEntry(string Name, bool IsFolder, long Size, DateTime ModifiedUtc);

/// <summary>The destination could not be reached or refused the login. Worded for the log.</summary>
public sealed class DestinationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One connection to the place a backup is stored. Every path is relative to the backup's folder and
/// uses forward slashes. An instance is used by one worker at a time.
/// </summary>
public interface IDestination : IAsyncDisposable
{
    /// <summary>Connects, and makes sure the backup's folder is there.</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>The entry at <paramref name="path"/>, or null when nothing is there.</summary>
    Task<DestinationEntry?> StatAsync(string path, CancellationToken ct);

    /// <summary>What is directly inside a folder. Empty when the folder doesn't exist.</summary>
    Task<List<DestinationEntry>> ListAsync(string folder, CancellationToken ct);

    /// <summary>Creates a folder and the folders above it. Does nothing when it exists.</summary>
    Task CreateFolderAsync(string folder, CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="content"/> to a file. With <paramref name="offset"/> zero the file is
    /// created or replaced. Otherwise the file must exist, and the content continues it from there.
    /// The folder must exist.
    /// </summary>
    Task WriteAsync(string path, Stream content, long offset, CancellationToken ct);

    /// <summary>Sets the file's modified time and returns its size, as the destination reports it.</summary>
    Task<long> SealAsync(string path, DateTime modifiedUtc, CancellationToken ct);

    Task ReadAsync(string path, Stream output, CancellationToken ct);

    /// <summary>Moves a file. Fails when something is already at <paramref name="to"/>.</summary>
    Task MoveAsync(string from, string to, CancellationToken ct);

    /// <summary>Deletes a file. Does nothing when it isn't there.</summary>
    Task DeleteFileAsync(string path, CancellationToken ct);

    /// <summary>Removes a folder if it is empty, and says whether it did.</summary>
    Task<bool> TryDeleteEmptyFolderAsync(string folder, CancellationToken ct);

    /// <summary>Deletes a folder and everything in it.</summary>
    Task DeleteTreeAsync(string folder, CancellationToken ct);
}

public interface IDestinationFactory
{
    /// <summary>A new connection, not yet connected.</summary>
    IDestination Open();
}

public static class DestinationFactory
{
    public static IDestinationFactory For(DestinationSpec spec, StateStore state, ILogger log)
        => spec.IsSftp ? new SftpDestinationFactory(spec, state, log) : new LocalDestinationFactory(spec);
}
