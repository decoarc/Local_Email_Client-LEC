// src/LEC.App/MoveMessageDialog.xaml.cs
using System.Windows;
using LEC.App.Converters;
using LEC.App.ViewModels;

namespace LEC.App;

public partial class MoveMessageDialog : Window
{
    public ProfileViewModel? SelectedProfile { get; private set; }
    public int? SelectedFolderId { get; private set; }

    public MoveMessageDialog(IEnumerable<ProfileViewModel> openProfiles, ProfileViewModel sourceProfile, int sourceFolderId)
    {
        InitializeComponent();

        var converter = (MoveDestinationValidityConverter)Resources["ValidityConverter"];
        converter.SourceProfile = sourceProfile;
        converter.SourceFolderId = sourceFolderId;

        ProfilesItemsControl.ItemsSource = openProfiles;
    }

    // Each open profile has its own TreeView (mirrors MainWindow's per-profile trees), so
    // this fires independently per tree. Deliberately doesn't clear a previously-selected
    // node's highlight in another tree when the user picks a different one -- unlike
    // MainWindow's FolderTreeView_SelectedItemChanged, this dialog closes as soon as one
    // pick is confirmed, so a stale highlight in an abandoned tree is a harmless, one-time
    // cosmetic detail, not a functional bug.
    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is System.Windows.Controls.TreeView { DataContext: ProfileViewModel profile }
            && e.NewValue is FolderNodeViewModel node)
        {
            SelectedProfile = profile;
            SelectedFolderId = node.Folder.Id;
            MoveButton.IsEnabled = true;
        }
    }

    private void MoveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
