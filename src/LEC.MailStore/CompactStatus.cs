namespace LEC.MailStore;

public sealed record CompactFolderStatus(
    int FolderId,
    string FolderName,
    long EstimatedReclaimableBytes);

public sealed record CompactStatus(IReadOnlyList<CompactFolderStatus> PendingFolders)
{
    public int PendingFolderCount => PendingFolders.Count;

    public long TotalEstimatedReclaimableBytes
    {
        get
        {
            long total = 0;
            foreach (var f in PendingFolders)
                total += f.EstimatedReclaimableBytes;
            return total;
        }
    }
}

/// <summary>
/// Progress for CompactAllPendingAsync. Report once per folder, immediately BEFORE that
/// folder's CompactFolderAsync starts, with CompletedCount = how many folders already
/// finished successfully (so the first Report is CompletedCount=0, CurrentFolderName=first).
/// </summary>
public sealed record CompactProgress(
    int CompletedCount,
    int TotalCount,
    string CurrentFolderName);
