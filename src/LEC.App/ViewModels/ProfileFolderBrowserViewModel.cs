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
            subfolderPaths = Directory.GetDirectories(path);
            dbxFilePaths = Directory.GetFiles(path, "*.dbx");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or System.Security.SecurityException)
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
