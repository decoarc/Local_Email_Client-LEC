// src/LEC.App/MoveFolderDialog.xaml.cs
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LEC.App.Converters;
using LEC.App.Services;
using LEC.App.ViewModels;

namespace LEC.App;

public partial class MoveFolderDialog : Window
{
    private readonly ProfileViewModel _sourceProfile;
    private readonly FolderNodeViewModel _sourceNode;

    public ProfileViewModel? SelectedProfile { get; private set; }
    public int? SelectedFolderId { get; private set; }

    public MoveFolderDialog(IEnumerable<ProfileViewModel> openProfiles, ProfileViewModel sourceProfile, FolderNodeViewModel sourceNode)
    {
        InitializeComponent();

        _sourceProfile = sourceProfile;
        _sourceNode = sourceNode;

        var converter = (MoveFolderDestinationValidityConverter)Resources["ValidityConverter"];
        converter.SourceNode = sourceNode;
        converter.SourceProfile = sourceProfile;

        ProfilesItemsControl.ItemsSource = openProfiles;
    }

    // Each open profile's own "Pastas locais (raiz)" label is always a valid destination for
    // that profile: root is nobody's descendant, and a no-op "already at root, same profile"
    // move is a harmless reorder, not an error. Clicking this label doesn't touch any
    // TreeView.SelectedItem, so there's no risk of TreeView_SelectedItemChanged firing and
    // overwriting this choice right after.
    private void RootLabel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { DataContext: ProfileViewModel profile })
            return;

        SelectedProfile = profile;
        SelectedFolderId = null;
        MoveButton.IsEnabled = true;
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is not TreeView { DataContext: ProfileViewModel profile } || e.NewValue is not FolderNodeViewModel node)
            return;

        bool isValid = MoveFolderDestinationRules.IsValidDestination(node, profile, _sourceNode, _sourceProfile);
        if (isValid)
        {
            SelectedProfile = profile;
            SelectedFolderId = node.Folder.Id;
        }
        else
        {
            SelectedProfile = null;
            SelectedFolderId = null;
        }

        MoveButton.IsEnabled = isValid;
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
