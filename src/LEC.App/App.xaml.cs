using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace LEC.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
    }

    /// <summary>
    /// Last-resort safety net: any exception that escapes an individual UI event handler
    /// (a bug, an unanticipated edge case in a real .dbx file, ...) must not hard-crash the
    /// app. Individual handlers in MainWindow already catch what they can anticipate and
    /// route it to a user-visible StatusText message; this is the backstop for everything
    /// else.
    /// </summary>
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Ocorreu um erro inesperado:\n\n{e.Exception.Message}", "LEC — Erro",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
