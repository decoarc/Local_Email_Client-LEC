using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LEC.App.ViewModels;
using Xunit;

namespace LEC.App.Tests;

public class ProfileFolderBrowserViewModelTests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lec-browser-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void NavigateTo_ListsDbxFilesSeparatelyFromOtherFilesAndSubfolders()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Sub"));
        File.WriteAllText(Path.Combine(temp.Path, "Inbox.dbx"), "");
        File.WriteAllText(Path.Combine(temp.Path, "Folders.dbx"), "");
        File.WriteAllText(Path.Combine(temp.Path, "readme.txt"), "");

        var vm = new ProfileFolderBrowserViewModel();
        vm.NavigateTo(temp.Path);

        Assert.Equal(temp.Path, vm.CurrentPath);
        Assert.Equal(new[] { "Sub" }, vm.Subfolders);
        Assert.Equal(new[] { "Folders.dbx", "Inbox.dbx" }, vm.DbxFiles);
    }

    [Fact]
    public void EnterSubfolder_DescendsAndListsThatFoldersDbxFiles()
    {
        using var temp = new TempDirectory();
        var subPath = Path.Combine(temp.Path, "Sub");
        Directory.CreateDirectory(subPath);
        File.WriteAllText(Path.Combine(subPath, "Sent.dbx"), "");

        var vm = new ProfileFolderBrowserViewModel();
        vm.NavigateTo(temp.Path);
        vm.EnterSubfolder("Sub");

        Assert.Equal(subPath, vm.CurrentPath);
        Assert.Equal(new[] { "Sent.dbx" }, vm.DbxFiles);
    }

    [Fact]
    public void EnterSubfolderDotDot_GoesBackUpToTheParentListing()
    {
        using var temp = new TempDirectory();
        var subPath = Path.Combine(temp.Path, "Sub");
        Directory.CreateDirectory(subPath);
        File.WriteAllText(Path.Combine(temp.Path, "Inbox.dbx"), "");

        var vm = new ProfileFolderBrowserViewModel();
        vm.NavigateTo(temp.Path);
        vm.EnterSubfolder("Sub");
        vm.EnterSubfolder("..");

        Assert.Equal(temp.Path, vm.CurrentPath);
        Assert.Equal(new[] { "Inbox.dbx" }, vm.DbxFiles);
    }

    [Fact]
    public void NavigateTo_FolderWithNoDbxFiles_LeavesDbxFilesEmpty()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "readme.txt"), "");

        var vm = new ProfileFolderBrowserViewModel();
        vm.NavigateTo(temp.Path);

        Assert.Empty(vm.DbxFiles);
    }

    [Fact]
    public void NavigateTo_InvalidPath_LeavesStateUnchanged()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "Inbox.dbx"), "");

        var vm = new ProfileFolderBrowserViewModel();
        vm.NavigateTo(temp.Path);
        vm.NavigateTo(Path.Combine(temp.Path, "does-not-exist"));

        Assert.Equal(temp.Path, vm.CurrentPath);
        Assert.Equal(new[] { "Inbox.dbx" }, vm.DbxFiles);
    }

    [Fact]
    public void InitialState_IsRootLevelListingDrives()
    {
        var vm = new ProfileFolderBrowserViewModel();

        Assert.Null(vm.CurrentPath);
        Assert.Empty(vm.DbxFiles);
        Assert.NotEmpty(vm.Subfolders);
        Assert.Contains(vm.Subfolders, d => d.StartsWith("C:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NavigateTo_MalformedPathWithStrayColon_LeavesStateUnchanged()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "Inbox.dbx"), "");

        var vm = new ProfileFolderBrowserViewModel();
        vm.NavigateTo(temp.Path);
        vm.NavigateTo(Path.Combine(temp.Path, "foo:bar"));

        Assert.Equal(temp.Path, vm.CurrentPath);
        Assert.Equal(new[] { "Inbox.dbx" }, vm.DbxFiles);
    }

    [Fact]
    public void NavigateTo_RaisesPropertyChangedForCurrentPath()
    {
        using var temp = new TempDirectory();
        var vm = new ProfileFolderBrowserViewModel();
        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        vm.NavigateTo(temp.Path);

        Assert.Contains(nameof(ProfileFolderBrowserViewModel.CurrentPath), raisedProperties);
    }

    [Fact]
    public void NavigateUp_AtRootLevel_IsANoOp()
    {
        var vm = new ProfileFolderBrowserViewModel();
        var subfoldersBefore = vm.Subfolders.ToList();

        vm.NavigateUp();

        Assert.Null(vm.CurrentPath);
        Assert.Equal(subfoldersBefore, vm.Subfolders);
    }

    [Fact]
    public void EnterSubfolder_AtRootLevel_TreatsNameAsADriveRoot()
    {
        var vm = new ProfileFolderBrowserViewModel();
        // Use the drive the test process is actually running from, not Subfolders[0] --
        // an unrelated drive letter (e.g. an empty CD-ROM drive) could be listed but
        // fail to navigate into, which would make this test flaky on some machines.
        var drive = Path.GetPathRoot(Path.GetTempPath())!;

        vm.EnterSubfolder(drive);

        Assert.Equal(drive, vm.CurrentPath);
    }
}
