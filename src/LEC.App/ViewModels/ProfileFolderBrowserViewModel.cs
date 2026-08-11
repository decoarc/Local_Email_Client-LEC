using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LEC.App.ViewModels;

public partial class ProfileFolderBrowserViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _currentPath;

    public ObservableCollection<string> Subfolders { get; } = new();
    public ObservableCollection<string> DbxFiles { get; } = new();

    public ProfileFolderBrowserViewModel() => NavigateToRoot();

    public void NavigateToRoot()
    {
        CurrentPath = null;

        Subfolders.Clear();
        foreach (var drive in Directory.GetLogicalDrives().OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            Subfolders.Add(drive);

        DbxFiles.Clear();
    }

    public void NavigateTo(string? path)
    {
        if (path is null)
        {
            NavigateToRoot();
            return;
        }

        string[] subfolderPaths;
        string[] dbxFilePaths;
        try
        {
            // Normalize before touching the filesystem: strips surrounding quotes from a
            // path copied via Explorer's "Copy as path", resolves any relative segments
            // against the current directory instead of storing a relative CurrentPath, and
            // is a no-op for an already-clean absolute path.
            path = Path.GetFullPath(path.Trim().Trim('"'));
            subfolderPaths = Directory.GetDirectories(path);
            // Win32 8.3 short-name matching can make "*.dbx" over-match (e.g. a file named
            // "Inbox.dbxbackup"); filter to a real ".dbx" suffix.
            dbxFilePaths = Directory.GetFiles(path, "*.dbx")
                .Where(f => f.EndsWith(".dbx", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or System.Security.SecurityException or NotSupportedException)
        {
            // Navigation failed -- leave CurrentPath/Subfolders/DbxFiles exactly as they were.
            return;
        }

        CurrentPath = path;

        Subfolders.Clear();
        foreach (var name in subfolderPaths.Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            Subfolders.Add(name!);

        DbxFiles.Clear();
        foreach (var name in dbxFilePaths.Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            DbxFiles.Add(name!);
    }

    public void NavigateUp()
    {
        if (CurrentPath is null)
            return;

        NavigateTo(Directory.GetParent(CurrentPath)?.FullName);
    }

    public void EnterSubfolder(string name)
    {
        if (name == "..")
        {
            NavigateUp();
            return;
        }

        NavigateTo(CurrentPath is null ? name : Path.Combine(CurrentPath, name));
    }
}
