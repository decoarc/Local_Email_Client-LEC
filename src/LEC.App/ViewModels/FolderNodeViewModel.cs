using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class FolderNodeViewModel : ObservableObject
{
    public StoreFolder Folder { get; }
    public ObservableCollection<FolderNodeViewModel> Children { get; } = new();

    public FolderNodeViewModel(StoreFolder folder) => Folder = folder;

    public string DisplayName => Folder.Name;
    public bool IsLoadable => Folder.IsLoadable;
    public bool IsSpecial => Folder.SpecialFolderKind != SpecialFolderKind.None;
}
