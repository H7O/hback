using HBack.Config;
using HBack.Destinations;
using HBack.State;

namespace HBack.Engine;

public sealed class VerifyResult
{
    public int Checked { get; set; }
    public List<string> Missing { get; } = [];
    public List<string> WrongSize { get; } = [];
    public bool NeverRan { get; set; }
    public bool Ok => Missing.Count == 0 && WrongSize.Count == 0;
}

/// <summary>
/// Checks hback's record against the destination: every file it believes is there should be there,
/// with the size it was copied at. It lists the destination folder by folder, so it reads no file
/// contents and is quick even over a slow link.
/// </summary>
public sealed class Verifier(StateStore state, ILogger log)
{
    /// <param name="fix">Forget the files that are missing or the wrong size, so the next run copies them again.</param>
    public async Task<VerifyResult> VerifyAsync(BackupJob job, bool fix, CancellationToken ct)
    {
        var result = new VerifyResult();
        if (await state.FindDestinationIdAsync(job.Destination.Id) is not { } destId)
        {
            result.NeverRan = true;
            return result;
        }

        var files = await state.LoadFilesAsync(destId);
        await using var session = DestinationFactory.For(job.Destination, state, log).Open();
        await session.ConnectAsync(ct);

        foreach (var folder in files.GroupBy(f => PathText.ParentOf(f.Key), StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var there = (await session.ListAsync(PathText.Join(BackupLayout.Current, folder.Key), ct))
                .Where(e => !e.IsFolder)
                .ToDictionary(e => e.Name, StringComparer.Ordinal);

            foreach (var (path, recorded) in folder)
            {
                result.Checked++;
                if (!there.TryGetValue(PathText.NameOf(path), out var entry))
                    result.Missing.Add(path);
                else if (entry.Size != recorded.Size)
                    result.WrongSize.Add(path);
                else
                    continue;

                if (fix) await state.DeleteFileAsync(destId, path);
            }
        }
        return result;
    }
}
