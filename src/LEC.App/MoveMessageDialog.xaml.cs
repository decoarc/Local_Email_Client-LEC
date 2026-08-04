// src/LEC.App/MoveMessageDialog.xaml.cs
using System.Windows;
using System.Windows.Controls;
using LEC.App.Converters;
using LEC.App.Services;
using LEC.App.ViewModels;

namespace LEC.App;

public partial class MoveMessageDialog : Window
{
    private readonly ProfileViewModel _sourceProfile;
    private readonly int _sourceFolderId;

    public ProfileViewModel? SelectedProfile { get; private set; }
    public int? SelectedFolderId { get; private set; }

    public MoveMessageDialog(IEnumerable<ProfileViewModel> openProfiles, ProfileViewModel sourceProfile, int sourceFolderId)
    {
        InitializeComponent();

        _sourceProfile = sourceProfile;
        _sourceFolderId = sourceFolderId;

        var converter = (MoveDestinationValidityConverter)Resources["ValidityConverter"];
        converter.SourceProfile = sourceProfile;
        converter.SourceFolderId = sourceFolderId;

        ProfilesItemsControl.ItemsSource = openProfiles;
    }

    // Every folder in every open profile's tree is always selectable now -- only the label
    // color hints at validity (see MoveDestinationValidityConverter's doc comment for why
    // disabling the container itself isn't an option in WPF). This handler is therefore the
    // ONLY place that actually enforces MoveDestinationRules.IsValidDestination: clicking an
    // invalid folder must leave nothing armed, in any tree. Each open profile has its own
    // TreeView, so this fires independently per tree -- clicking an invalid node in a
    // DIFFERENT tree than a previously-valid pick must clear that previous pick, not just
    // leave it silently armed with the wrong destination.
    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (sender is not TreeView { DataContext: ProfileViewModel profile } || e.NewValue is not FolderNodeViewModel node)
            return;

        bool isValid = MoveDestinationRules.IsValidDestination(node.Folder, profile, _sourceProfile, _sourceFolderId);
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
