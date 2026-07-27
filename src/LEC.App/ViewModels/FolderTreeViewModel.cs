using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class FolderTreeViewModel : ObservableObject
{
    public ObservableCollection<FolderNodeViewModel> RootNodes { get; } = new();

    public void Build(IReadOnlyList<StoreFolder> folders)
    {
        RootNodes.Clear();

        var nodesById = new Dictionary<int, FolderNodeViewModel>();
        foreach (var folder in folders)
            nodesById[folder.Id] = new FolderNodeViewModel(folder);

        foreach (var folder in folders)
        {
            var node = nodesById[folder.Id];

            // A ParentId that doesn't resolve to a known folder (root sentinel already
            // mapped to null by DbxStore, or a genuine dangling reference like the real
            // profile's folder 73 -> 53) becomes a top-level node.
            if (folder.ParentId is int parentId && nodesById.TryGetValue(parentId, out var parentNode))
                parentNode.Children.Add(node);
            else
                RootNodes.Add(node);
        }
    }
}
