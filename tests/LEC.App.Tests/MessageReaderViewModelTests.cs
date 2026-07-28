// tests/LEC.App.Tests/MessageReaderViewModelTests.cs
using System.Text;
using LEC.App.ViewModels;
using LEC.MailStore;
using MimeKit;
using Xunit;

namespace LEC.App.Tests;

public class MessageReaderViewModelTests
{
    [Fact]
    public async Task LoadsAndRendersTheSelectedMessage()
    {
        var reference = new MessageRef(1, 100);
        byte[] raw = Encoding.ASCII.GetBytes("From: a@b.com\r\nSubject: Ola\r\n\r\nCorpo");
        var store = new FakeMailStore(
            folders: Array.Empty<StoreFolder>(),
            rawByRef: new() { [reference] = raw });

        var vm = new MessageReaderViewModel(store);
        await vm.LoadAsync(reference);

        Assert.NotNull(vm.Current);
        Assert.Equal("Ola", vm.Current!.Subject);
    }

    [Fact]
    public async Task SaveAttachmentAsyncRereadsTheMessageAndWritesTheChosenAttachment()
    {
        var reference = new MessageRef(1, 100);

        var builder = new BodyBuilder { HtmlBody = "<p>Ola</p>" };
        byte[] attachmentBytes = Encoding.ASCII.GetBytes("conteudo do anexo");
        builder.Attachments.Add("relatorio.txt", attachmentBytes, ContentType.Parse("text/plain"));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ana", "ana@example.com"));
        message.Subject = "Ola";
        message.Body = builder.ToMessageBody();

        var messageStream = new MemoryStream();
        message.WriteTo(messageStream);
        byte[] raw = messageStream.ToArray();

        var store = new FakeMailStore(
            folders: Array.Empty<StoreFolder>(),
            rawByRef: new() { [reference] = raw });

        var vm = new MessageReaderViewModel(store);
        await vm.LoadAsync(reference);

        string destinationPath = Path.Combine(Path.GetTempPath(), $"lec-attachment-test-{Guid.NewGuid():N}.txt");
        try
        {
            await vm.SaveAttachmentAsync(0, destinationPath);

            byte[] written = await File.ReadAllBytesAsync(destinationPath);
            Assert.Equal(attachmentBytes, written);
        }
        finally
        {
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public async Task SaveAttachmentAsyncThrowsWhenNoMessageHasBeenLoadedYet()
    {
        var store = new FakeMailStore(folders: Array.Empty<StoreFolder>());
        var vm = new MessageReaderViewModel(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            vm.SaveAttachmentAsync(0, Path.Combine(Path.GetTempPath(), "wont-be-created.txt")));
    }
}
