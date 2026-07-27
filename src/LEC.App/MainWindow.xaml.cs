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
    private LEC.App.Services.ImageBlockingWebView2Handler? _imageBlocker;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await ReaderWebView.EnsureCoreWebView2Async();
        _imageBlocker = new LEC.App.Services.ImageBlockingWebView2Handler(ReaderWebView.CoreWebView2);
        ReaderWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
    }

    private void CoreWebView2_NavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        // NavigateToString's own content loads through a data: URI — never intercept that.
        if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;
        var result = MessageBox.Show($"Abrir este link no navegador?\n\n{e.Uri}", "Confirmar link",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
    }

    private void ShowImages_Click(object sender, RoutedEventArgs e)
    {
        if (_imageBlocker is null)
            return;

        _imageBlocker.BlockImages = false;
        ImagesBlockedBar.Visibility = Visibility.Collapsed;
        ReaderWebView.Reload();
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
            if (_imageBlocker is not null)
            {
                _imageBlocker.BlockImages = true;
                ImagesBlockedBar.Visibility = Visibility.Visible;
            }

            await _viewModel.SelectMessageAsync(header);
            if (_viewModel.MessageReader?.Current is { } rendered)
                ReaderWebView.NavigateToString(rendered.Html);
        }
    }
}
