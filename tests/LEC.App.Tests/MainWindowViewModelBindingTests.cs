// tests/LEC.App.Tests/MainWindowViewModelBindingTests.cs
using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Data;
using LEC.App.ViewModels;
using LEC.MailStore;
using Xunit;

namespace LEC.App.Tests;

/// <summary>
/// Regression coverage for the C1 finding from the Fase 2 final review: MainWindow.xaml
/// binds ItemsSource="{Binding MessageList.Messages}", which WPF resolves once while
/// MessageList is still null (before OpenProfile ever runs). If MessageList/MessageReader
/// are plain get-only properties with a private setter, WPF never learns the property
/// later became non-null -- OpenProfile() populates real data but the bound control never
/// re-resolves the path, so the list silently stays empty forever.
///
/// This test drives a real System.Windows.Data.Binding against a real
/// BindingOperations.SetBinding call -- proving actual WPF binding-refresh behavior -- but
/// creates no Window and never calls Show()/renders anything, staying within the Global
/// Constraint that forbids testing XAML rendering while still covering binding-resolution
/// logic.
/// </summary>
public class MainWindowViewModelBindingTests
{
    // WPF's FrameworkElement/InputManager machinery asserts the calling thread is STA
    // ("O thread de chamada deve ser STA..."), which xunit's default test-execution
    // thread is not. There's no live Window/rendering involved -- these tests only need
    // an STA thread to legally construct a bindable WPF control at all -- so run each
    // test body on a dedicated STA thread rather than pulling in a third-party test
    // package for this alone.
    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    [Fact]
    public void MessageListPropertyChangeIsObservableViaWpfBinding() => RunOnSta(() =>
    {
        var vm = new MainWindowViewModel();
        var target = new ListView();
        BindingOperations.SetBinding(
            target,
            ItemsControl.ItemsSourceProperty,
            new Binding("MessageList.Messages") { Source = vm });

        // WPF resolved the binding path against a null MessageList, same as MainWindow at
        // startup, before any profile has been opened.
        Assert.Null(target.ItemsSource);

        var messageList = new MessageListViewModel(new FakeMailStore(Array.Empty<StoreFolder>()));
        vm.MessageList = messageList;

        // With [ObservableProperty] raising PropertyChanged("MessageList"), the bound
        // ListView re-resolves the path and picks up the now-populated collection --
        // without it, this would still be null, reproducing the bug the real headless
        // probe found (Messages.Count == 2235 but 0 rows visible).
        Assert.Same(messageList.Messages, target.ItemsSource);
    });

    [Fact]
    public void MessageReaderPropertyChangeIsObservableViaWpfBinding() => RunOnSta(() =>
    {
        var vm = new MainWindowViewModel();
        var target = new ContentControl();
        BindingOperations.SetBinding(
            target,
            ContentControl.ContentProperty,
            new Binding("MessageReader") { Source = vm });

        Assert.Null(target.Content);

        var reader = new MessageReaderViewModel(new FakeMailStore(Array.Empty<StoreFolder>()));
        vm.MessageReader = reader;

        Assert.Same(reader, target.Content);
    });
}
