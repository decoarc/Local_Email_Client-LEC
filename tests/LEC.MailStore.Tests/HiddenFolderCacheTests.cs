// tests/LEC.MailStore.Tests/HiddenFolderCacheTests.cs
using System.Linq;
using Xunit;

namespace LEC.MailStore.Tests;

public class HiddenFolderCacheTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lec-hidden-folder-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void GetHiddenFolderIdsReturnsEmptyWhenNothingMarked()
    {
        using var cache = new HiddenFolderCache(TempDbPath());

        var ids = cache.GetHiddenFolderIds(@"C:\profiles\BCP");

        Assert.Empty(ids);
    }

    [Fact]
    public void MarkHiddenThenGetHiddenFolderIdsRoundTrips()
    {
        string dbPath = TempDbPath();

        using (var cache = new HiddenFolderCache(dbPath))
            cache.MarkHidden(@"C:\profiles\BCP", 7);

        using (var reopened = new HiddenFolderCache(dbPath))
        {
            var ids = reopened.GetHiddenFolderIds(@"C:\profiles\BCP");
            Assert.Contains(7, ids);
        }
    }

    [Fact]
    public void MarkHiddenTwiceForTheSameFolderIsIdempotent()
    {
        using var cache = new HiddenFolderCache(TempDbPath());

        cache.MarkHidden(@"C:\p", 100);
        cache.MarkHidden(@"C:\p", 100); // must not throw

        Assert.Single(cache.GetHiddenFolderIds(@"C:\p"));
    }

    [Fact]
    public void MarkHiddenForMultipleFoldersInTheSameProfileAccumulates()
    {
        using var cache = new HiddenFolderCache(TempDbPath());

        cache.MarkHidden(@"C:\p", 1);
        cache.MarkHidden(@"C:\p", 2);
        cache.MarkHidden(@"C:\p", 3);

        Assert.Equal(new[] { 1, 2, 3 }, cache.GetHiddenFolderIds(@"C:\p").OrderBy(x => x));
    }

    [Fact]
    public void DifferentProfilesWithTheSameFolderIdDoNotCollide()
    {
        using var cache = new HiddenFolderCache(TempDbPath());

        cache.MarkHidden(@"C:\profileA", 5);
        cache.MarkHidden(@"C:\profileB", 5);

        Assert.Equal(new[] { 5 }, cache.GetHiddenFolderIds(@"C:\profileA"));
        Assert.Equal(new[] { 5 }, cache.GetHiddenFolderIds(@"C:\profileB"));

        // Hiding folder 5 in profile A must not affect profile B's independent record of it.
        using var otherCache = new HiddenFolderCache(TempDbPath());
        otherCache.MarkHidden(@"C:\profileA", 6);
        Assert.DoesNotContain(6, otherCache.GetHiddenFolderIds(@"C:\profileB"));
    }
}
