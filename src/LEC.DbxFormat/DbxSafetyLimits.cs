namespace LEC.DbxFormat;

/// <summary>
/// Defensive ceilings against a corrupted file, per ARQUITETURA.md §7.3.
/// These are our own choices, not part of the reverse-engineered format —
/// chosen generously so real (even large, decades-old) mailboxes parse fully.
/// </summary>
public static class DbxSafetyLimits
{
    public const int MaxIndexTreeDepth = 64;
    public const int MaxMessageBlockFragments = 200_000; // ~100 MB at 512 B/fragment
    public const long MaxMessageSizeBytes = 200L * 1024 * 1024; // 200 MB per reassembled message
}
