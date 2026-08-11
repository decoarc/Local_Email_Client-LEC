using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LEC.MailStore;

namespace LEC.App.ViewModels;

public partial class FolderTreeViewModel : ObservableObject
{
    public ObservableCollection<FolderNodeViewModel> RootNodes { get; } = new();

    public void Build(IReadOnlyList<StoreFolder> folders)
    {
        // Build() always replaces every node with a brand-new instance, even for
        // folders untouched by whatever change triggered this rebuild -- the WPF
        // TreeView tracks expanded/collapsed state per item instance, so without this
        // every subfolder the user had open would collapse on any folder create,
        // delete, or move.
        var expandedIds = new HashSet<int>();
        CollectExpandedIds(RootNodes, expandedIds);

        RootNodes.Clear();

        var nodesById = new Dictionary<int, FolderNodeViewModel>();
        foreach (var folder in folders)
            nodesById[folder.Id] = new FolderNodeViewModel(folder) { IsExpanded = expandedIds.Contains(folder.Id) };

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

        // A folder whose ParentId participates in a cycle (self-reference, or A -> B -> A)
        // resolves successfully as a child of its cycle-mate above and never reaches
        // RootNodes via the pass that just ran -- it isn't dropped by a crash, it's dropped
        // by silently succeeding in the wrong way. Sweep for any node unreachable from an
        // actual root and surface it as a root too, so a cycle degrades into "extra
        // top-level folders" instead of "folder vanished".
        var reached = new HashSet<int>();
        var stack = new Stack<FolderNodeViewModel>(RootNodes);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!reached.Add(current.Folder.Id))
                continue;

            foreach (var child in current.Children)
                stack.Push(child);
        }

        foreach (var folder in folders)
        {
            if (!reached.Contains(folder.Id))
                RootNodes.Add(nodesById[folder.Id]);
        }
    }

    private static void CollectExpandedIds(IEnumerable<FolderNodeViewModel> nodes, HashSet<int> expandedIds)
    {
        foreach (var node in nodes)
        {
            if (node.IsExpanded)
                expandedIds.Add(node.Folder.Id);

            CollectExpandedIds(node.Children, expandedIds);
        }
    }
}
