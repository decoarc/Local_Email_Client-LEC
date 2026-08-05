// src/LEC.App/MoveFolderDialog.xaml.cs
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LEC.App.Converters;
using LEC.App.Services;
using LEC.App.ViewModels;

namespace LEC.App;

public partial class MoveFolderDialog : Window
{
    private readonly FolderNodeViewModel _sourceNode;

    public int? SelectedFolderId { get; private set; }

    public MoveFolderDialog(ProfileViewModel profile, FolderNodeViewModel sourceNode)
    {
        InitializeComponent();

        _sourceNode = sourceNode;

        var converter = (MoveFolderDestinationValidityConverter)Resources["ValidityConverter"];
        converter.SourceNode = sourceNode;

        FolderTreeView.ItemsSource = profile.FolderTree.RootNodes;
    }

    // "Pastas locais (raiz)" is always a valid destination: moving a folder to the root
    // can never create a cycle (root is nobody's descendant), and DbxStore.MoveFolderAsync
    // treats a no-op "already at root" move as a harmless reorder rather than an error.
    // Clicking this label doesn't touch TreeView.SelectedItem, so there's no risk of
    // TreeView_SelectedItemChanged firing and overwriting this right after.
    private void RootLabel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SelectedFolderId = null;
        MoveButton.IsEnabled = true;
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not FolderNodeViewModel node)
            return;

        bool isValid = MoveFolderDestinationRules.IsValidDestination(node, _sourceNode);
        SelectedFolderId = isValid ? node.Folder.Id : null;
        MoveButton.IsEnabled = isValid;
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
