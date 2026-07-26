// tests/LEC.DbxFormat.Tests/DbxFolderInfoTests.cs
using System.Text;
using LEC.DbxFormat;
using Xunit;

namespace LEC.DbxFormat.Tests;

public class DbxFolderInfoTests
{
    [Fact]
    public void ReadsFolderFieldsFromAnInfoNode()
    {
        // Mirrors the real BCP/Folders.dbx entry for "Caixa de entrada" (Inbox):
        // folder id=96, parent=1, backed by "Caixa de entrada (1).dbx", 2235 messages.
        var builder = new TestDbxBuilder();
        builder.WriteHeader(DbxFileType.Folder);

        int infoNodeOffset = builder.WriteInfoNode(new[]
        {
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FolderId, Direct = true, DirectValue = 96 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.ParentId, Direct = true, DirectValue = 1 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.Name, Data = Encoding.UTF8.GetBytes("Caixa de entrada\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.FileName, Data = Encoding.UTF8.GetBytes("Caixa de entrada (1).dbx\0") },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.MessageCount, Direct = true, DirectValue = 2235 },
            new TestDbxBuilder.AttributeSpec { FieldId = (byte)DbxFolderFieldId.SpecialFolderType, Direct = true, DirectValue = 1 },
        });

        using var source = new ByteArrayDbxSource(builder.ToArray());
        var info = DbxFolderInfo.Read(source, infoNodeOffset);

        Assert.Equal(96, info.FolderId);
        Assert.Equal(1, info.ParentId);
        Assert.Equal("Caixa de entrada", info.Name);
        Assert.Equal("Caixa de entrada (1).dbx", info.FileName);
        Assert.Equal(2235, info.MessageCount);
        Assert.Equal(DbxSpecialFolderType.Inbox, info.SpecialFolderType);
    }
}
