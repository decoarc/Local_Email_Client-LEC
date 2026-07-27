namespace LEC.MailStore;

public sealed class StoreFolder
{
    public required int Id { get; init; }

    /// <summary>
    /// Null means "no parent" (this is a root). A non-null value that doesn't
    /// match any other StoreFolder's Id is a real possibility (a dangling/orphan
    /// reference) — tree construction must tolerate it, not this type.
    /// </summary>
    public int? ParentId { get; init; }

    public required string Name { get; init; }
    public SpecialFolderKind SpecialFolderKind { get; init; }
    public int MessageCount { get; init; }

    /// <summary>
    /// Cheap, upfront signal (backing file exists on disk, or there is no
    /// backing file at all) — not a guarantee the file will actually parse
    /// successfully; EnumerateHeadersAsync degrades gracefully either way.
    /// </summary>
    public required bool IsLoadable { get; init; }
}
