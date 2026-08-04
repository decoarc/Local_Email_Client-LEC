using System.Linq;
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxEmailFileWriterTests
{
    [Fact]
    public void CreateEmptyProducesAReadableEmptyMessageFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-email-{Guid.NewGuid():N}.dbx");
        try
        {
            DbxEmailFileWriter.CreateEmpty(path);

            using var file = DbxEmailFile.Open(path);

            Assert.Equal(StoreHealth.Ok, file.Health);
            Assert.Empty(file.Messages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateEmptyIsDetectedAsAnEmailTypeFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-email-{Guid.NewGuid():N}.dbx");
        try
        {
            DbxEmailFileWriter.CreateEmpty(path);

            using var source = new MemoryMappedDbxSource(path);
            Assert.True(DbxHeader.TryDetectType(source, out var type));
            Assert.Equal(DbxFileType.Email, type);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildRoundTripsASingleSmallMessage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-messages-{Guid.NewGuid():N}.dbx");
        try
        {
            byte[] raw = Encoding.UTF8.GetBytes("From: a@x.com\r\nSubject: Oi\r\n\r\nCorpo da mensagem.");
            var entry = new DbxMessageWriteEntry(raw, "Oi", "Ana", "ana@x.com", "<msg-1@x.com>",
                new DateTime(2004, 3, 1, 10, 0, 0, DateTimeKind.Utc));

            DbxEmailFileWriter.Rebuild(path, new[] { entry });

            using var file = DbxEmailFile.Open(path);
            Assert.Equal(StoreHealth.Ok, file.Health);
            var info = Assert.Single(file.Messages);

            Assert.Equal("Oi", info.Subject);
            Assert.Equal("Ana", info.SenderName);
            Assert.Equal("ana@x.com", info.SenderAddress);
            Assert.Equal("<msg-1@x.com>", info.MessageId);
            Assert.Equal(new DateTime(2004, 3, 1, 10, 0, 0, DateTimeKind.Utc), info.SendTimeUtc);
            Assert.Equal((uint)raw.Length, info.MessageSize);

            byte[] rawBack = file.ReadMessageRaw(info, out bool truncated);
            Assert.False(truncated);
            Assert.Equal(raw, rawBack);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildRoundTripsTheReceiverFields()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-messages-{Guid.NewGuid():N}.dbx");
        try
        {
            var receiveTime = new DateTime(2004, 3, 2, 8, 30, 0, DateTimeKind.Utc);
            var entry = new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("corpo"), "Oi", "Ana", "ana@x.com",
                "<msg-1@x.com>", new DateTime(2004, 3, 1, 10, 0, 0, DateTimeKind.Utc), Flags: 0,
                ReceiverName: "Bruno", ReceiverAddress: "bruno@y.com", ReceiveTimeUtc: receiveTime);

            DbxEmailFileWriter.Rebuild(path, new[] { entry });

            using var file = DbxEmailFile.Open(path);
            var info = Assert.Single(file.Messages);

            Assert.Equal("Bruno", info.ReceiverName);
            Assert.Equal("bruno@y.com", info.ReceiverAddress);
            Assert.Equal(receiveTime, info.ReceiveTimeUtc);
            // ... without disturbing the sender side.
            Assert.Equal("Ana", info.SenderName);
            Assert.Equal(new DateTime(2004, 3, 1, 10, 0, 0, DateTimeKind.Utc), info.SendTimeUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildOmitsTheReceiverFieldsWhenTheyAreNull()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-messages-{Guid.NewGuid():N}.dbx");
        try
        {
            DbxEmailFileWriter.Rebuild(path, new[]
            {
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("corpo"), "Oi", null, null, null, null),
            });

            using var file = DbxEmailFile.Open(path);
            var info = Assert.Single(file.Messages);

            Assert.Equal(StoreHealth.Ok, file.Health);
            Assert.Null(info.ReceiverName);
            Assert.Null(info.ReceiverAddress);
            Assert.Null(info.ReceiveTimeUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildSplitsALargeMessageAcrossMultipleBlocksAndReadsItBackWhole()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-messages-{Guid.NewGuid():N}.dbx");
        try
        {
            // 3 full 512-byte blocks plus a partial 4th -- forces the writer's chaining path.
            byte[] raw = Enumerable.Range(0, DbxFormat.MessageBlockMaxDataSize * 3 + 100)
                .Select(i => (byte)(i % 251)).ToArray();
            var entry = new DbxMessageWriteEntry(raw, "Anexo grande", null, null, null, null);

            DbxEmailFileWriter.Rebuild(path, new[] { entry });

            using var file = DbxEmailFile.Open(path);
            var info = Assert.Single(file.Messages);
            byte[] rawBack = file.ReadMessageRaw(info, out bool truncated);

            Assert.False(truncated);
            Assert.Equal(raw, rawBack);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildRoundTripsMultipleMessagesPreservingOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-messages-{Guid.NewGuid():N}.dbx");
        try
        {
            var entries = new[]
            {
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("um"), "Um", null, null, null, null),
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("dois"), "Dois", null, null, null, null),
            };

            DbxEmailFileWriter.Rebuild(path, entries);

            using var file = DbxEmailFile.Open(path);
            Assert.Equal(2, file.Messages.Count);
            Assert.Contains(file.Messages, m => m.Subject == "Um");
            Assert.Contains(file.Messages, m => m.Subject == "Dois");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RebuildThrowsWhenGivenMoreThan255Messages()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lec-messages-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Messages.dbx");
        try
        {
            var entries = Enumerable.Range(0, 256)
                .Select(i => new DbxMessageWriteEntry(Encoding.UTF8.GetBytes($"m{i}"), $"S{i}", null, null, null, null))
                .ToArray();

            Assert.Throws<ArgumentException>(() => DbxEmailFileWriter.Rebuild(path, entries));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RebuildOverwritesAnExistingFileAndLeavesNoTempFileBehind()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lec-messages-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Messages.dbx");
        try
        {
            DbxEmailFileWriter.Rebuild(path, new[] { new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("um"), "Um", null, null, null, null) });
            DbxEmailFileWriter.Rebuild(path, new[]
            {
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("um"), "Um", null, null, null, null),
                new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("dois"), "Dois", null, null, null, null),
            });

            using var file = DbxEmailFile.Open(path);
            Assert.Equal(2, file.Messages.Count);
            Assert.Single(Directory.GetFiles(dir)); // only Messages.dbx — no leftover .tmp-* file
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RebuildLeavesNoTempFileBehindWhenTheFinalMoveFails()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"lec-messages-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "Messages.dbx");
        try
        {
            DbxEmailFileWriter.Rebuild(path, new[] { new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("um"), "Um", null, null, null, null) });

            // An exclusive handle on the destination makes Rebuild's final File.Move fail
            // *after* the temp file was already written — the only window where a stray
            // ".tmp-*" file can survive.
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.ThrowsAny<Exception>(() => DbxEmailFileWriter.Rebuild(path, new[]
                {
                    new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("um"), "Um", null, null, null, null),
                    new DbxMessageWriteEntry(Encoding.UTF8.GetBytes("dois"), "Dois", null, null, null, null),
                }));
            }

            Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RebuildThrowsOnAnEmptyRawMessage()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lec-messages-{Guid.NewGuid():N}.dbx");

        var entry = new DbxMessageWriteEntry(Array.Empty<byte>(), "Vazio", null, null, null, null);

        Assert.Throws<ArgumentException>(() => DbxEmailFileWriter.Rebuild(path, new[] { entry }));
        Assert.False(File.Exists(path));
    }
}
