using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LEC.App.ViewModels;

namespace LEC.App;

public partial class OpenProfileDialog : Window
{
    private readonly ProfileFolderBrowserViewModel _browser = new();
    private readonly ObservableCollection<string> _subfoldersDisplay = new();
    private bool _subfoldersRefreshQueued;

    public string? SelectedPath { get; private set; }

    public OpenProfileDialog()
    {
        InitializeComponent();

        SubfoldersListBox.ItemsSource = _subfoldersDisplay;
        DbxFilesListBox.ItemsSource = _browser.DbxFiles;

        _browser.Subfolders.CollectionChanged += (_, _) => QueueSubfoldersRefresh();
        _browser.DbxFiles.CollectionChanged += (_, _) => RefreshNoDbxMessage();
        _browser.PropertyChanged += Browser_PropertyChanged;

        RefreshSubfoldersDisplay();
        RefreshNoDbxMessage();
        UpdateFromCurrentPath();
    }

    private void Browser_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileFolderBrowserViewModel.CurrentPath))
            UpdateFromCurrentPath();
    }

    private void UpdateFromCurrentPath()
    {
        PathTextBox.Text = _browser.CurrentPath ?? "";
        SelectButton.IsEnabled = _browser.CurrentPath != null;
    }

    // NavigateTo populates Subfolders one Add() at a time, so a folder with N
    // subfolders fires N+1 CollectionChanged events. Rebuilding the mirrored display
    // list on every single one is O(N^2) and can visibly stall the UI thread on a
    // large directory (e.g. C:\Windows\WinSxS). Coalesce a burst of events from one
    // synchronous NavigateTo call into a single deferred refresh instead.
    private void QueueSubfoldersRefresh()
    {
        if (_subfoldersRefreshQueued)
            return;

        _subfoldersRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _subfoldersRefreshQueued = false;
            RefreshSubfoldersDisplay();
        }));
    }

    private void RefreshSubfoldersDisplay()
    {
        _subfoldersDisplay.Clear();
        if (_browser.CurrentPath != null)
            _subfoldersDisplay.Add("..");
        foreach (var name in _browser.Subfolders)
            _subfoldersDisplay.Add(name);
    }

    private void RefreshNoDbxMessage()
    {
        NoDbxMessage.Visibility = _browser.DbxFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SubfoldersListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SubfoldersListBox, (DependencyObject)e.OriginalSource) is not ListBoxItem)
            return;

        if (SubfoldersListBox.SelectedItem is string name)
            _browser.EnterSubfolder(name);
    }

    private void PathTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _browser.NavigateTo(PathTextBox.Text);
            // On success this just re-sets the same value UpdateFromCurrentPath already
            // wrote via the CurrentPath PropertyChanged handler. On failure (invalid path),
            // CurrentPath didn't change, so this reverts the box instead of leaving the
            // user's bad input displayed with no feedback.
            PathTextBox.Text = _browser.CurrentPath ?? "";
            e.Handled = true;
        }
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        // The user may have typed/pasted a path and clicked Select without pressing
        // Enter first -- commit whatever is in the box before reading CurrentPath, or
        // the dialog would silently confirm a different folder than the one displayed.
        if (PathTextBox.Text != (_browser.CurrentPath ?? ""))
            _browser.NavigateTo(PathTextBox.Text);

        if (_browser.CurrentPath is null)
            return;

        SelectedPath = _browser.CurrentPath;
        DialogResult = true;
    }
}
