// src/LEC.App/MainWindow.xaml.cs
using System.Windows;
using System.Windows.Controls;
using LEC.App.ViewModels;
using LEC.MailStore;
using Microsoft.Win32;

namespace LEC.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecione a pasta do perfil (contendo Folders.dbx)" };
        if (dialog.ShowDialog() == true)
            _viewModel.OpenProfile(dialog.FolderName);
    }

    private async void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNodeViewModel node)
            await _viewModel.SelectFolderAsync(node);
    }

    private async void MessageListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MessageListView.SelectedItem is MessageHeader header)
        {
            await _viewModel.SelectMessageAsync(header);
            if (_viewModel.MessageReader?.Current is { } rendered)
                ReaderWebView.NavigateToString(rendered.Html);
        }
    }
}
