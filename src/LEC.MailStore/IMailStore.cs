using LEC.DbxFormat;

namespace LEC.MailStore;

/// <summary>
/// Core contract from ARQUITETURA.md §7.1. Extended in Fase 3 (varredura) with
/// scavenge-related members -- the Fase 2 plan's "don't add members" was scoped to
/// that plan, not a permanent freeze. See docs/superpowers/specs/2026-07-27-fase3-varredura-design.md §4.
/// </summary>
public interface IMailStore : IDisposable
{
    IReadOnlyList<StoreFolder> Folders { get; }

    IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, CancellationToken ct = default);

    Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct = default);

    StoreHealth Health { get; }

    /// <summary>
    /// This specific folder's own backing-file health (distinct from the aggregate
    /// <see cref="Health"/>, which only reflects Folders.dbx). Opens the folder if it
    /// isn't already open (same lazy-open behavior as EnumerateHeadersAsync); returns
    /// StoreHealth.Ok if the folder can't be opened at all (IsLoadable already covers
    /// that failure mode separately). This is the correct signal for "does this folder
    /// have an inconsistency scavenging might help with" -- a per-message Health.Partial
    /// only catches one specific corruption shape (an out-of-range attribute pointer on
    /// an already-indexed message); an orphaned block coexisting with perfectly healthy
    /// indexed messages -- the scenario ARQUITETURA.md §7.5 exists for -- never sets any
    /// message's Health to Partial, but does set the file's own DbxEmailFile.Health.
    /// </summary>
    StoreHealth GetFolderHealth(StoreFolder folder);

    /// <summary>Current scavenge status for a folder -- reads the cache, runs nothing.</summary>
    ScavengeStatus GetScavengeStatus(StoreFolder folder);

    /// <summary>
    /// Runs the scavenge scan for a folder and persists the result. Never automatic --
    /// only ever called in direct response to an explicit user action (ARQUITETURA.md §7.4).
    /// </summary>
    Task<ScavengeStatus> ScavengeAsync(StoreFolder folder, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Toggles whether a folder's already-scavenged items appear in EnumerateHeadersAsync.</summary>
    void SetScavengeVisibility(StoreFolder folder, bool visible);

    /// <summary>
    /// Creates a folder — a real backing file plus a new Folders.dbx entry for DbxStore.
    /// See DbxStore.CreateFolderAsync for the full contract (validation, backups,
    /// exceptions thrown).
    /// </summary>
    Task<StoreFolder> CreateFolderAsync(string name, int? parentId, CancellationToken ct = default);

    /// <summary>
    /// Appends a message to a folder in THIS store — a from-scratch rewrite of that
    /// folder's backing .dbx file. Has no idea where the raw bytes came from; a
    /// cross-store "move message" orchestration (spanning two IMailStore instances) is
    /// responsible for reading them from wherever the message currently lives first.
    /// See DbxStore.AddMessageAsync for the full contract.
    /// </summary>
    Task AddMessageAsync(int folderId, byte[] rawMessage, string? subject, string? senderName,
        string? senderAddress, string? messageId, DateTime? dateUtc, CancellationToken ct = default);

    /// <summary>
    /// Appends several messages to one folder with a single backup and a single rebuild of
    /// that folder's .dbx. Empty <paramref name="messages"/> is a no-op. See
    /// DbxStore.AddMessagesAsync.
    /// </summary>
    Task AddMessagesAsync(int folderId, IReadOnlyList<OutgoingMessage> messages, CancellationToken ct = default);

    /// <summary>
    /// Marks a message as moved away from its folder — EnumerateHeadersAsync stops
    /// returning it from now on. Never writes to the backing .dbx; this is purely a
    /// local, app-side "hide" marker (see MovedMessageCache). A no-op, not an error, if
    /// called twice for the same message.
    /// </summary>
    Task HideMovedMessageAsync(MessageRef id, CancellationToken ct = default);

    /// <summary>
    /// Marks a message as deleted — EnumerateHeadersAsync stops returning it from now on.
    /// Never writes to the backing .dbx; purely a local, app-side "hide" marker (see
    /// DeletedMessageCache), kept separate from HideMovedMessageAsync's marker even though
    /// the mechanism is identical. A no-op, not an error, if called twice for the same
    /// message.
    /// </summary>
    Task DeleteMessageAsync(MessageRef id, CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes a folder — and every folder in its subtree — from Folders.dbx
    /// and from disk. See DbxStore.DeleteFolderAsync for the full contract (special-folder
    /// guard, subtree collection, exceptions thrown). Unlike DeleteMessageAsync, calling
    /// this twice for the same id throws on the second call.
    /// </summary>
    Task DeleteFolderAsync(int folderId, CancellationToken ct = default);

    /// <summary>
    /// Moves a folder to a new parent within this store — a from-scratch rewrite of
    /// Folders.dbx that changes only the moved folder's ParentId entry. See
    /// DbxStore.MoveFolderAsync for the full contract (cycle guard, name-collision guard,
    /// exceptions thrown). Same-store only — no cross-profile move.
    /// </summary>
    Task MoveFolderAsync(int folderId, int? newParentId, CancellationToken ct = default);

    /// <summary>
    /// Folders in this store that still have soft-deleted or moved message bytes in their
    /// backing .dbx (see MovedMessageCache / DeletedMessageCache). Empty when nothing to reclaim.
    /// </summary>
    Task<CompactStatus> GetCompactStatusAsync(CancellationToken ct = default);

    Task CompactFolderAsync(int folderId, CancellationToken ct = default);

    Task CompactAllPendingAsync(
        IProgress<CompactProgress>? progress = null,
        CancellationToken ct = default);
}
