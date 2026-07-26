namespace LEC.DbxFormat;

/// <summary>
/// Thrown only when a file isn't a .dbx at all (signature mismatch) — a
/// programmer/caller error, distinct from in-content corruption which is
/// reported via StoreHealth instead of an exception (ARQUITETURA.md §7.4).
/// </summary>
public sealed class DbxFormatException : Exception
{
    public DbxFormatException(string message) : base(message) { }
}
