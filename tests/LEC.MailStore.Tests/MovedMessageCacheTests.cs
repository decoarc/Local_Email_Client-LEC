// tests/LEC.MailStore.Tests/MovedMessageCacheTests.cs
using Xunit;

namespace LEC.MailStore.Tests;

public class MovedMessageCacheTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lec-moved-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void GetMovedOffsetsReturnsEmptyWhenNothingMarked()
    {
        using var cache = new MovedMessageCache(TempDbPath());

        var offsets = cache.GetMovedOffsets(@"C:\profiles\BCP", "Caixa de entrada.dbx");

        Assert.Empty(offsets);
    }

    [Fact]
    public void MarkMovedThenGetMovedOffsetsRoundTrips()
    {
        string dbPath = TempDbPath();

        using (var cache = new MovedMessageCache(dbPath))
            cache.MarkMoved(@"C:\profiles\BCP", "Caixa de entrada.dbx", 500);

        using (var reopened = new MovedMessageCache(dbPath))
        {
            var offsets = reopened.GetMovedOffsets(@"C:\profiles\BCP", "Caixa de entrada.dbx");
            Assert.Contains(500, offsets);
        }
    }

    [Fact]
    public void MarkMovedTwiceForTheSameOffsetIsIdempotent()
    {
        using var cache = new MovedMessageCache(TempDbPath());

        cache.MarkMoved(@"C:\p", "f.dbx", 100);
        cache.MarkMoved(@"C:\p", "f.dbx", 100); // must not throw

        Assert.Single(cache.GetMovedOffsets(@"C:\p", "f.dbx"));
    }

    [Fact]
    public void DifferentProfilesWithTheSameFolderFileNameDoNotCollide()
    {
        using var cache = new MovedMessageCache(TempDbPath());

        cache.MarkMoved(@"C:\profileA", "Caixa de entrada.dbx", 1);
        cache.MarkMoved(@"C:\profileB", "Caixa de entrada.dbx", 2);

        Assert.Equal(new[] { 1 }, cache.GetMovedOffsets(@"C:\profileA", "Caixa de entrada.dbx"));
        Assert.Equal(new[] { 2 }, cache.GetMovedOffsets(@"C:\profileB", "Caixa de entrada.dbx"));
    }
}
