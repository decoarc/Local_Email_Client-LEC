// tests/LEC.App.Tests/MessageReaderViewModelTests.cs
using System.Text;
using LEC.App.ViewModels;
using LEC.MailStore;
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
}
