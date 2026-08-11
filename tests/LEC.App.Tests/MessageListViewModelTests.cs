// tests/LEC.App.Tests/MessageListViewModelTests.cs
using System.Linq;
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

public class MessageListViewModelTests
{
    [Fact]
    public async Task LoadsHeadersFromTheStoreForTheSelectedFolder()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var header = new MessageHeader { Id = new MessageRef(1, 100), Subject = "Ola" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { header } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        Assert.Single(vm.Messages);
        Assert.Equal("Ola", vm.Messages[0].Subject);
        Assert.Null(vm.StatusText);
    }

    [Fact]
    public async Task SetsAStatusMessageForANonLoadableFolder()
    {
        var folder = new StoreFolder { Id = 1, Name = "Quebrada", IsLoadable = false, HasBackingFile = true };
        var store = new FakeMailStore(folders: new[] { folder });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        Assert.Empty(vm.Messages);
        Assert.NotNull(vm.StatusText);
    }

    [Fact]
    public async Task ClearsPreviousMessagesWhenLoadingADifferentFolder()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(
            folders: new[] { folderA, folderB },
            headersByFolder: new()
            {
                [1] = new() { new MessageHeader { Id = new MessageRef(1, 1), Subject = "Da A" } },
            });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folderA);
        Assert.Single(vm.Messages);

        await vm.LoadAsync(folderB);
        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task SortsMessagesNewestFirstByDate()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var older = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Antigo", Date = new DateTime(2001, 1, 1) };
        var newer = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Novo", Date = new DateTime(2005, 1, 1) };
        var noDate = new MessageHeader { Id = new MessageRef(1, 3), Subject = "SemData", Date = null };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { older, newer, noDate } }); // deliberately out of order

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        Assert.Equal(new[] { "Novo", "Antigo", "SemData" }, vm.Messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task SortBySenderOrdersAlphabeticallyAscendingOnFirstClick()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var bravo = new MessageHeader { Id = new MessageRef(1, 1), Subject = "B", SenderName = "Bravo" };
        var alfa = new MessageHeader { Id = new MessageRef(1, 2), Subject = "A", SenderName = "alfa" };
        var charlie = new MessageHeader { Id = new MessageRef(1, 3), Subject = "C", SenderName = "Charlie" };
        var noSender = new MessageHeader { Id = new MessageRef(1, 4), Subject = "D", SenderName = null };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { bravo, alfa, charlie, noSender } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SortBySender();

        // Null SenderName is compared as string.Empty, which sorts before any non-empty
        // name in ascending (culture-insensitive) order -- so the null-sender message ("D")
        // lands first.
        Assert.Equal(new[] { "D", "A", "B", "C" }, vm.Messages.Select(m => m.Subject));
        Assert.Equal("De ▲", vm.SenderColumnHeader);
        Assert.Equal("Data", vm.DateColumnHeader);
    }

    [Fact]
    public async Task SortBySenderTwiceReversesToDescending()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var alfa = new MessageHeader { Id = new MessageRef(1, 1), Subject = "A", SenderName = "Alfa" };
        var bravo = new MessageHeader { Id = new MessageRef(1, 2), Subject = "B", SenderName = "Bravo" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { alfa, bravo } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SortBySender();
        vm.SortBySender();

        Assert.Equal(new[] { "B", "A" }, vm.Messages.Select(m => m.Subject));
        Assert.Equal("De ▼", vm.SenderColumnHeader);
    }

    [Fact]
    public async Task SortByDateOnceFromDefaultReversesToOldestFirst()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var older = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Antigo", Date = new DateTime(2001, 1, 1) };
        var newer = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Novo", Date = new DateTime(2005, 1, 1) };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { older, newer } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder); // default: Date descendente (mais recente primeiro)

        vm.SortByDate(); // mesma coluna já ativa -> inverte

        Assert.Equal(new[] { "Antigo", "Novo" }, vm.Messages.Select(m => m.Subject));
        Assert.Equal("Data ▲", vm.DateColumnHeader);
        Assert.Equal("De", vm.SenderColumnHeader);
    }

    [Fact]
    public async Task SwitchingColumnAlwaysStartsAscendingRegardlessOfPriorDirection()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var older = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Antigo", Date = new DateTime(2001, 1, 1), SenderName = "Zeta" };
        var newer = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Novo", Date = new DateTime(2005, 1, 1), SenderName = "Alfa" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { older, newer } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SortByDate();   // Date -> ascendente (mais antiga primeiro)
        vm.SortByDate();   // Date -> descendente (mais recente primeiro)
        vm.SortBySender(); // troca de coluna -> sempre ascendente, mesmo vindo de Date descendente

        Assert.Equal(new[] { "Novo", "Antigo" }, vm.Messages.Select(m => m.Subject)); // Alfa, Zeta
        Assert.Equal("De ▲", vm.SenderColumnHeader);
        Assert.Equal("Data", vm.DateColumnHeader);
    }

    [Fact]
    public async Task SortChoicePersistsAcrossFolderReload()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(
            folders: new[] { folderA, folderB },
            headersByFolder: new()
            {
                [1] = new() { new MessageHeader { Id = new MessageRef(1, 1), Subject = "A1", SenderName = "Bravo" } },
                [2] = new()
                {
                    new MessageHeader { Id = new MessageRef(2, 1), Subject = "B-Bravo", SenderName = "Bravo" },
                    new MessageHeader { Id = new MessageRef(2, 2), Subject = "B-Alfa", SenderName = "Alfa" },
                },
            });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folderA);
        vm.SortBySender();

        await vm.LoadAsync(folderB);

        Assert.Equal(new[] { "B-Alfa", "B-Bravo" }, vm.Messages.Select(m => m.Subject));
        Assert.Equal("De ▲", vm.SenderColumnHeader);
    }

    [Fact]
    public async Task FiltersBySenderNameCaseInsensitively()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var joao = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Assunto A", SenderName = "Joao Silva" };
        var maria = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Assunto B", SenderName = "Maria Souza" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { joao, maria } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SearchField = MessageSearchField.Sender;
        vm.SearchText = "JOAO";

        Assert.Equal(new[] { "Assunto A" }, vm.Messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task FiltersBySubjectCaseInsensitively()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var relatorio = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Relatorio mensal" };
        var outro = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Reuniao" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { relatorio, outro } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder); // SearchField já começa em Subject por padrão

        vm.SearchText = "RELATORIO";

        Assert.Equal(new[] { "Relatorio mensal" }, vm.Messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task EmptySearchTextShowsAllMessages()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var a = new MessageHeader { Id = new MessageRef(1, 1), Subject = "A", SenderName = "Alfa" };
        var b = new MessageHeader { Id = new MessageRef(1, 2), Subject = "B", SenderName = "Bravo" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { a, b } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);

        vm.SearchField = MessageSearchField.Sender;
        vm.SearchText = "Alfa";
        Assert.Single(vm.Messages);

        vm.SearchText = string.Empty;
        Assert.Equal(2, vm.Messages.Count);
    }

    [Fact]
    public async Task SwitchingSearchFieldReappliesImmediatelyWithExistingText()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        // "Ana" aparece no remetente da primeira mensagem e no assunto da segunda -- prova que
        // é o toggle, não o texto, que decide o que é comparado.
        var bySender = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Assunto qualquer", SenderName = "Ana Paula" };
        var bySubject = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Fotos da Ana", SenderName = "Carlos" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { bySender, bySubject } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);
        vm.SearchText = "Ana";

        vm.SearchField = MessageSearchField.Sender;
        Assert.Equal(new[] { "Assunto qualquer" }, vm.Messages.Select(m => m.Subject));

        vm.SearchField = MessageSearchField.Subject;
        Assert.Equal(new[] { "Fotos da Ana" }, vm.Messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task LoadAsyncResetsSearchTextAndFieldToDefaults()
    {
        var folderA = new StoreFolder { Id = 1, Name = "A", IsLoadable = true, HasBackingFile = true };
        var folderB = new StoreFolder { Id = 2, Name = "B", IsLoadable = true, HasBackingFile = true };
        var store = new FakeMailStore(
            folders: new[] { folderA, folderB },
            headersByFolder: new()
            {
                [1] = new() { new MessageHeader { Id = new MessageRef(1, 1), Subject = "A1", SenderName = "Alfa" } },
                [2] = new() { new MessageHeader { Id = new MessageRef(2, 1), Subject = "B1", SenderName = "Bravo" } },
            });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folderA);
        vm.SearchField = MessageSearchField.Sender;
        vm.SearchText = "Alfa";

        await vm.LoadAsync(folderB);

        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Equal(MessageSearchField.Subject, vm.SearchField);
        Assert.Single(vm.Messages); // sem filtro, mostra a única mensagem da pasta B
    }

    [Fact]
    public async Task FilterComposesWithSort()
    {
        var folder = new StoreFolder { Id = 1, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true };
        var bravo = new MessageHeader { Id = new MessageRef(1, 1), Subject = "Relatorio B", SenderName = "Bravo" };
        var alfa = new MessageHeader { Id = new MessageRef(1, 2), Subject = "Relatorio A", SenderName = "Alfa" };
        var outro = new MessageHeader { Id = new MessageRef(1, 3), Subject = "Outro assunto", SenderName = "Charlie" };
        var store = new FakeMailStore(
            folders: new[] { folder },
            headersByFolder: new() { [1] = new() { bravo, alfa, outro } });

        var vm = new MessageListViewModel(store);
        await vm.LoadAsync(folder);
        vm.SearchText = "Relatorio"; // SearchField padrão = Subject

        vm.SortBySender();

        Assert.Equal(new[] { "Relatorio A", "Relatorio B" }, vm.Messages.Select(m => m.Subject));
    }
}
