// tests/LEC.MailStore.Tests/DeletedMessageCacheTests.cs
using Xunit;

namespace LEC.MailStore.Tests;

public class DeletedMessageCacheTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lec-deleted-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void GetDeletedOffsetsReturnsEmptyWhenNothingMarked()
    {
        using var cache = new DeletedMessageCache(TempDbPath());

        var offsets = cache.GetDeletedOffsets(@"C:\profiles\BCP", "Caixa de entrada.dbx");

        Assert.Empty(offsets);
    }

    [Fact]
    public void MarkDeletedThenGetDeletedOffsetsRoundTrips()
    {
        string dbPath = TempDbPath();

        using (var cache = new DeletedMessageCache(dbPath))
            cache.MarkDeleted(@"C:\profiles\BCP", "Caixa de entrada.dbx", 500);

        using (var reopened = new DeletedMessageCache(dbPath))
        {
            var offsets = reopened.GetDeletedOffsets(@"C:\profiles\BCP", "Caixa de entrada.dbx");
            Assert.Contains(500, offsets);
        }
    }

    [Fact]
    public void MarkDeletedTwiceForTheSameOffsetIsIdempotent()
    {
        using var cache = new DeletedMessageCache(TempDbPath());

        cache.MarkDeleted(@"C:\p", "f.dbx", 100);
        cache.MarkDeleted(@"C:\p", "f.dbx", 100); // must not throw

        Assert.Single(cache.GetDeletedOffsets(@"C:\p", "f.dbx"));
    }

    [Fact]
    public void ClearFolderRemovesEveryOffsetForThatFolderOnly()
    {
        using var cache = new DeletedMessageCache(TempDbPath());

        cache.MarkDeleted(@"C:\p", "alvo.dbx", 100);
        cache.MarkDeleted(@"C:\p", "alvo.dbx", 200);
        cache.MarkDeleted(@"C:\p", "outra.dbx", 300);
        cache.MarkDeleted(@"C:\outroPerfil", "alvo.dbx", 400);

        cache.ClearFolder(@"C:\p", "alvo.dbx");

        Assert.Empty(cache.GetDeletedOffsets(@"C:\p", "alvo.dbx"));
        Assert.Equal(new[] { 300 }, cache.GetDeletedOffsets(@"C:\p", "outra.dbx"));
        Assert.Equal(new[] { 400 }, cache.GetDeletedOffsets(@"C:\outroPerfil", "alvo.dbx"));
    }

    [Fact]
    public void ClearFolderOnAFolderThatWasNeverMarkedIsANoOp()
    {
        using var cache = new DeletedMessageCache(TempDbPath());

        cache.ClearFolder(@"C:\p", "nunca-tocada.dbx"); // must not throw

        Assert.Empty(cache.GetDeletedOffsets(@"C:\p", "nunca-tocada.dbx"));
    }

    [Fact]
    public void DifferentProfilesWithTheSameFolderFileNameDoNotCollide()
    {
        using var cache = new DeletedMessageCache(TempDbPath());

        cache.MarkDeleted(@"C:\profileA", "Caixa de entrada.dbx", 1);
        cache.MarkDeleted(@"C:\profileB", "Caixa de entrada.dbx", 2);

        Assert.Equal(new[] { 1 }, cache.GetDeletedOffsets(@"C:\profileA", "Caixa de entrada.dbx"));
        Assert.Equal(new[] { 2 }, cache.GetDeletedOffsets(@"C:\profileB", "Caixa de entrada.dbx"));
    }
}
