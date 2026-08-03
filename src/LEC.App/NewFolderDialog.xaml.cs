// src/LEC.App/NewFolderDialog.xaml.cs
using System.Windows;
using System.Windows.Input;

namespace LEC.App;

public partial class NewFolderDialog : Window
{
    public string FolderName { get; private set; } = "";

    public NewFolderDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameTextBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

    private void NameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Accept();
    }

    private void Accept()
    {
        FolderName = NameTextBox.Text;
        DialogResult = true;
    }
}
