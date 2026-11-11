using System.Security.Cryptography;
using System.Text;

namespace HBack.Engine;

/// <summary>
/// Held while a run writes to a destination, so the service and a manual <c>hback run</c> never write
/// to the same destination at once. It is a file held open exclusively, which the operating system
/// releases by itself if hback dies.
/// </summary>
public sealed class RunLock : IDisposable
{
    private readonly FileStream _file;

    private RunLock(FileStream file) => _file = file;

    /// <summary>The lock, or null when another run holds it.</summary>
    /// <param name="patience">How long to keep trying. A run waits a moment, because
    /// <c>hback status</c> takes the lock for an instant to see whether a run is going, and that
    /// glance must not make a run skip itself. The glance itself passes zero.</param>
    public static RunLock? TryAcquire(string lockDir, string destinationId, TimeSpan patience = default)
    {
        Directory.CreateDirectory(lockDir);
        var path = Path.Combine(lockDir, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(destinationId)))[..16] + ".lock");
        var until = DateTime.UtcNow + patience;
        while (true)
        {
            try
            {
                return new RunLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held by another run. (Windows answers "access denied" while the holder is deleting
                // the file on its way out.)
                if (DateTime.UtcNow >= until) return null;
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose() => _file.Dispose();
}
