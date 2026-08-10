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

    public string? SelectedPath { get; private set; }

    public OpenProfileDialog()
    {
        InitializeComponent();

        SubfoldersListBox.ItemsSource = _subfoldersDisplay;
        DbxFilesListBox.ItemsSource = _browser.DbxFiles;

        _browser.Subfolders.CollectionChanged += (_, _) => RefreshSubfoldersDisplay();
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
            e.Handled = true;
        }
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedPath = _browser.CurrentPath;
        DialogResult = true;
    }
}
