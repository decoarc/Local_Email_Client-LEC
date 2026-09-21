// src/LEC.App/CompactProgressDialog.xaml.cs
using System.ComponentModel;
using System.Windows;
using LEC.App.ViewModels;
using LEC.MailStore;

namespace LEC.App;

public partial class CompactProgressDialog : Window
{
    private enum CompactMode { SingleFolder, OneProfile, ManyProfiles }

    private readonly MainWindowViewModel _viewModel;
    private readonly CompactMode _mode;
    private readonly ProfileViewModel? _profile;
    private readonly IReadOnlyList<ProfileViewModel>? _profiles;
    private readonly int _folderId;
    private readonly string? _folderName;

    private bool _finished;

    public Exception? Failure { get; private set; }

    public CompactProgressDialog(MainWindowViewModel vm, ProfileViewModel profile, int folderId, string folderName)
    {
        InitializeComponent();

        _viewModel = vm;
        _mode = CompactMode.SingleFolder;
        _profile = profile;
        _folderId = folderId;
        _folderName = folderName;

        StatusText.Text = $"Compactando \"{folderName}\"…";
        Loaded += CompactProgressDialog_Loaded;
    }

    public CompactProgressDialog(MainWindowViewModel vm, ProfileViewModel profile)
    {
        InitializeComponent();

        _viewModel = vm;
        _mode = CompactMode.OneProfile;
        _profile = profile;

        StatusText.Text = "Compactando pastas…";
        Loaded += CompactProgressDialog_Loaded;
    }

    public CompactProgressDialog(MainWindowViewModel vm, IReadOnlyList<ProfileViewModel> profiles)
    {
        InitializeComponent();

        _viewModel = vm;
        _mode = CompactMode.ManyProfiles;
        _profiles = profiles;

        StatusText.Text = "Compactando pastas…";
        Loaded += CompactProgressDialog_Loaded;
    }

    private async void CompactProgressDialog_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= CompactProgressDialog_Loaded;

        try
        {
            switch (_mode)
            {
                case CompactMode.SingleFolder:
                    await RunSingleFolderAsync();
                    break;
                case CompactMode.OneProfile:
                    await RunOneProfileAsync();
                    break;
                default:
                    await RunManyProfilesAsync();
                    break;
            }

            _finished = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Failure = ex;
            ShowFinalState(ex.Message);
        }
    }

    private Task RunSingleFolderAsync()
    {
        ProgressBar.IsIndeterminate = true;
        return _viewModel.CompactFolderAsync(_profile!, _folderId);
    }

    private Task RunOneProfileAsync()
    {
        var progress = new SynchronousProgress<CompactProgress>(p =>
            ApplyWorkingProgress(p.CurrentFolderName, p.CompletedCount, p.TotalCount));
        return _viewModel.CompactAllPendingAsync(_profile!, progress);
    }

    private async Task RunManyProfilesAsync()
    {
        var aggregated = await _viewModel.GetAggregatedCompactStatusAsync();
        int globalTotal = aggregated.PendingFolderCount;
        int globalCompleted = 0;

        foreach (var profile in _profiles!)
        {
            var status = await _viewModel.GetCompactStatusAsync(profile);
            if (status.PendingFolderCount == 0)
                continue;

            int offset = globalCompleted;
            var progress = new SynchronousProgress<CompactProgress>(p =>
                ApplyWorkingProgress(p.CurrentFolderName, offset + p.CompletedCount, globalTotal));

            await _viewModel.CompactAllPendingAsync(profile, progress);
            globalCompleted += status.PendingFolderCount;
        }
    }

    private void ApplyWorkingProgress(string currentFolderName, int completedCount, int totalCount)
    {
        if (totalCount <= 0)
            return;

        int displayIndex = completedCount + 1;
        StatusText.Text = $"Compactando \"{currentFolderName}\"… ({displayIndex}/{totalCount})";
        ProgressBar.Value = displayIndex / (double)totalCount;
    }

    private void ShowFinalState(string message)
    {
        _finished = true;
        ProgressBar.IsIndeterminate = false;
        StatusText.Text = message;
        CloseButton.Visibility = Visibility.Visible;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // Title-bar X must not dismiss an in-flight compact — there is no cancel path.
    // Once success sets DialogResult or failure reaches ShowFinalState, _finished is
    // true and this lets the close through.
    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_finished)
            return;

        e.Cancel = true;
    }

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
