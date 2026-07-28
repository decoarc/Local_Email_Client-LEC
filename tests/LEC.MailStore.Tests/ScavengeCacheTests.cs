// tests/LEC.MailStore.Tests/ScavengeCacheTests.cs
using Xunit;

namespace LEC.MailStore.Tests;

public class ScavengeCacheTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lec-scavenge-test-{Guid.NewGuid():N}.db");

    [Fact]
    public void GetScanReturnsNullWhenNeverScanned()
    {
        using var cache = new ScavengeCache(TempDbPath());

        var result = cache.GetScan(@"C:\profiles\BCP", "Caixa de entrada.dbx");

        Assert.Null(result);
    }

    [Fact]
    public void SaveScanThenGetScanRoundTripsTheItems()
    {
        string dbPath = TempDbPath();
        var items = new[]
        {
            new CachedRecoveredItem(500, 120, RecoveredMessageKind.Recovered, "Assunto", "Ana", "ana@x.com", new DateTime(2003, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
            new CachedRecoveredItem(900, 40, RecoveredMessageKind.Fragment, null, null, null, null),
        };

        using (var cache = new ScavengeCache(dbPath))
            cache.SaveScan(@"C:\profiles\BCP", "Caixa de entrada.dbx", items);

        using (var reopened = new ScavengeCache(dbPath))
        {
            var result = reopened.GetScan(@"C:\profiles\BCP", "Caixa de entrada.dbx");

            Assert.NotNull(result);
            Assert.False(result!.IsVisible); // default: hidden until explicitly shown
            Assert.Equal(2, result.Items.Count);
            var recovered = Assert.Single(result.Items, i => i.Kind == RecoveredMessageKind.Recovered);
            Assert.Equal("Assunto", recovered.Subject);
            Assert.Equal("Ana", recovered.SenderName);
            var fragment = Assert.Single(result.Items, i => i.Kind == RecoveredMessageKind.Fragment);
            Assert.Null(fragment.Subject);
        }
    }

    [Fact]
    public void SetVisibilityPersistsAcrossReopen()
    {
        string dbPath = TempDbPath();
        var items = new[] { new CachedRecoveredItem(1, 1, RecoveredMessageKind.Recovered, "S", null, null, null) };

        using (var cache = new ScavengeCache(dbPath))
        {
            cache.SaveScan(@"C:\p", "f.dbx", items);
            cache.SetVisibility(@"C:\p", "f.dbx", visible: true);
        }

        using (var reopened = new ScavengeCache(dbPath))
        {
            var result = reopened.GetScan(@"C:\p", "f.dbx");
            Assert.True(result!.IsVisible);
        }
    }

    [Fact]
    public void SaveScanReplacesAnyPreviousResultForTheSameFolder()
    {
        string dbPath = TempDbPath();

        using var cache = new ScavengeCache(dbPath);
        cache.SaveScan(@"C:\p", "f.dbx", new[] { new CachedRecoveredItem(1, 1, RecoveredMessageKind.Recovered, "Primeiro", null, null, null) });
        cache.SaveScan(@"C:\p", "f.dbx", new[] { new CachedRecoveredItem(2, 2, RecoveredMessageKind.Fragment, "Segundo", null, null, null) });

        var result = cache.GetScan(@"C:\p", "f.dbx");

        var item = Assert.Single(result!.Items);
        Assert.Equal("Segundo", item.Subject);
    }

    [Fact]
    public void DifferentProfilesWithTheSameFolderFileNameDoNotCollide()
    {
        string dbPath = TempDbPath();
        using var cache = new ScavengeCache(dbPath);

        cache.SaveScan(@"C:\profileA", "Caixa de entrada.dbx", new[] { new CachedRecoveredItem(1, 1, RecoveredMessageKind.Recovered, "A", null, null, null) });
        cache.SaveScan(@"C:\profileB", "Caixa de entrada.dbx", new[] { new CachedRecoveredItem(2, 2, RecoveredMessageKind.Recovered, "B", null, null, null) });

        Assert.Equal("A", Assert.Single(cache.GetScan(@"C:\profileA", "Caixa de entrada.dbx")!.Items).Subject);
        Assert.Equal("B", Assert.Single(cache.GetScan(@"C:\profileB", "Caixa de entrada.dbx")!.Items).Subject);
    }
}
