using System.Security.Cryptography;

namespace HBack.Engine;

/// <summary>A source file could not be read. The destination is not to blame, so the copy is not retried.</summary>
public sealed class SourceFileException(string message, Exception inner) : Exception(message, inner)
{
    /// <summary>True when the read failed in the middle of an upload, which leaves the connection unusable.</summary>
    public bool DuringTransfer { get; init; }
}

/// <summary>
/// Reads a source file for an upload, and on the way hashes the bytes and counts them. So the hash
/// that is stored is the hash of exactly what was sent, with no second read of the file.
/// </summary>
public sealed class HashingReadStream(Stream inner, IncrementalHash hash) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n;
        try
        {
            n = inner.Read(buffer);
        }
        catch (IOException ex)
        {
            throw new SourceFileException(ex.Message, ex) { DuringTransfer = true };
        }
        hash.AppendData(buffer[..n]);
        BytesRead += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n;
        try
        {
            n = await inner.ReadAsync(buffer, cancellationToken);
        }
        catch (IOException ex)
        {
            throw new SourceFileException(ex.Message, ex) { DuringTransfer = true };
        }
        hash.AppendData(buffer.Span[..n]);
        BytesRead += n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
