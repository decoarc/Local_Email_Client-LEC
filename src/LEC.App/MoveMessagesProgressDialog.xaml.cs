// src/LEC.App/MoveMessagesProgressDialog.xaml.cs
using System.ComponentModel;
using System.Windows;
using LEC.App.ViewModels;
using LEC.MailStore;

namespace LEC.App;

public partial class MoveMessagesProgressDialog : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly ProfileViewModel _sourceProfile;
    private readonly IReadOnlyList<MessageHeader> _messages;
    private readonly ProfileViewModel _destinationProfile;
    private readonly int _destinationFolderId;
    private readonly CancellationTokenSource _cts = new();

    private int _succeeded;
    private bool _finished;

    public Exception? Failure { get; private set; }

    public MoveMessagesProgressDialog(MainWindowViewModel viewModel, ProfileViewModel sourceProfile,
        IReadOnlyList<MessageHeader> messages, ProfileViewModel destinationProfile, int destinationFolderId)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _sourceProfile = sourceProfile;
        _messages = messages;
        _destinationProfile = destinationProfile;
        _destinationFolderId = destinationFolderId;

        StatusText.Text = $"Movendo mensagens... 0 de {_messages.Count}";
        Loaded += MoveMessagesProgressDialog_Loaded;
    }

    private async void MoveMessagesProgressDialog_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MoveMessagesProgressDialog_Loaded;

        // System.Progress<T>.Report always marshals via SynchronizationContext.Post -- queued,
        // never synchronous, even from this same thread -- which would let a cancellation land
        // between a Report call and the queued callback that applies it, showing a stale count.
        // SynchronousProgress applies the update immediately and in-order instead; safe to touch
        // ProgressBar/StatusText directly since this always runs on this window's dispatcher thread.
        var progress = new SynchronousProgress<double>(p =>
        {
            _succeeded = (int)Math.Round(p * _messages.Count);
            ProgressBar.Value = p;
            StatusText.Text = $"Movendo mensagens... {_succeeded} de {_messages.Count} ({p:P0})";
        });

        try
        {
            await _viewModel.MoveMessagesAsync(_sourceProfile, _messages, _destinationProfile, _destinationFolderId, progress, _cts.Token);
            _finished = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            ShowFinalState($"Cancelado: {_succeeded} de {_messages.Count} mensagens movidas.");
        }
        catch (Exception ex)
        {
            Failure = ex;
            ShowFinalState(ex.Message);
        }
    }

    private void ShowFinalState(string message)
    {
        _finished = true;
        StatusText.Text = message;
        CancelButton.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        _cts.Cancel();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // Closing the window (title-bar X) while the move is still running must behave exactly
    // like clicking "Cancelar" -- and must not let the window actually close yet, since the
    // background Rebuild could still be mid-write. Once the Loaded handler's catch block
    // reaches ShowFinalState (or the success path calls Close() itself), _finished is true
    // and this lets the close through.
    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_finished)
            return;

        _cts.Cancel();
        e.Cancel = true;
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
