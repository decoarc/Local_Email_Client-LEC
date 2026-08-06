// src/LEC.App/MainWindow.xaml.cs
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LEC.App.Services;
using LEC.App.ViewModels;
using LEC.DbxFormat;
using LEC.MailStore;
using Microsoft.Win32;

namespace LEC.App;

public partial class MainWindow : Window
{
    // WebView2's NavigateToString throws above ~2 MB of HTML. MessageRenderer caps its own
    // fallback output, but this is an independent second cap at the call site -- defense in
    // depth against a future renderer change (or an HtmlBody from a real message) producing
    // something too large, so the reading pane degrades instead of crashing either way.
    private const int MaxNavigateHtmlChars = 1_500_000;

    // Reading pane is a passive viewer for old, untrusted email -- links are only ever
    // opened after explicit user confirmation, and only for schemes we know how to hand
    // off safely.
    private static readonly string[] AllowedLinkSchemes = { "http", "https", "mailto" };

    private readonly MainWindowViewModel _viewModel = new();
    private LEC.App.Services.ImageBlockingWebView2Handler? _imageBlocker;
    private CancellationTokenSource? _scavengeCts;
    // The profile the in-flight scan (if any) is actually running against -- distinct from
    // _selectedProfile, which can move to a different profile's tree while the scan keeps
    // running in the background (see ScavengeButton_Click's scanProfile/scanNode snapshot).
    private ProfileViewModel? _scavengeProfile;
    private ProfileViewModel? _selectedProfile;
    private FolderNodeViewModel? _selectedNode;
    private TreeView? _selectedTreeView;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ReaderWebView.EnsureCoreWebView2Async();

            var core = ReaderWebView.CoreWebView2;

            // This is a local reader for old mail, not a general browser: no script
            // execution, no script-driven side channels (postMessage, JS dialogs).
            core.Settings.IsScriptEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;

            _imageBlocker = new LEC.App.Services.ImageBlockingWebView2Handler(core);
            core.NavigationStarting += CoreWebView2_NavigationStarting;
            core.NewWindowRequested += CoreWebView2_NewWindowRequested;
        }
        catch (Exception ex)
        {
            // Most likely cause: the WebView2 Evergreen runtime isn't installed on this
            // machine. The app must stay usable (folder/message list still work) rather
            // than crash on startup.
            _viewModel.StatusText = $"Não foi possível iniciar o painel de leitura: {ex.Message}";
        }
    }

    private void CoreWebView2_NavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        // NavigateToString's own content loads through a data: URI — never intercept that.
        if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return;

        e.Cancel = true;
        ConfirmAndOpenLink(e.Uri);
    }

    private void CoreWebView2_NewWindowRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
    {
        // A target="_blank" link or window.open() would otherwise open a brand new
        // WebView2 window that completely bypasses the link-confirmation flow above.
        e.Handled = true;
        ConfirmAndOpenLink(e.Uri);
    }

    private void ConfirmAndOpenLink(string uri)
    {
        try
        {
            bool schemeAllowed = Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                && AllowedLinkSchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase);

            if (!schemeAllowed)
            {
                MessageBox.Show($"Tipo de link não suportado:\n\n{uri}", "Link não suportado",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"Abrir este link no navegador?\n\n{uri}", "Confirmar link",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível abrir o link: {ex.Message}";
        }
    }

    private void ShowImages_Click(object sender, RoutedEventArgs e)
    {
        if (_imageBlocker is null)
            return;

        _imageBlocker.BlockImages = false;
        ImagesBlockedBar.Visibility = Visibility.Collapsed;
        ReaderWebView.Reload();
    }

    private async void SaveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AttachmentInfo attachment || _viewModel.MessageReader is null)
            return;

        var dialog = new SaveFileDialog { FileName = SanitizeAttachmentFileName(attachment.FileName) };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            await _viewModel.MessageReader.SaveAttachmentAsync(attachment.Index, dialog.FileName);
            _viewModel.StatusText = $"Anexo salvo em {dialog.FileName}";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível salvar o anexo: {ex.Message}";
        }
    }

    // attachment.FileName comes straight from a MimePart's Content-Disposition/Content-Type
    // header -- i.e. from an old, untrusted .dbx email, never validated as safe. SaveFileDialog
    // treats FileName as a path expression it pre-fills and resolves on Save (e.g.
    // "..\..\evil.exe" or an absolute path into a startup folder), so this untrusted value must
    // be reduced to a bare, harmless file name before it ever reaches the dialog. The
    // destination the user ultimately picks via the native dialog needs no further validation --
    // only this pre-fill value does.
    private static string SanitizeAttachmentFileName(string fileName)
    {
        string name = Path.GetFileName(fileName);

        char[] invalid = Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalid) >= 0)
        {
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            name = builder.ToString();
        }

        return string.IsNullOrEmpty(name) ? "anexo" : name;
    }

    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecione a pasta do perfil (contendo Folders.dbx)" };
        if (dialog.ShowDialog() == true)
            _viewModel.OpenProfile(dialog.FolderName);
    }

    private void CloseProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileViewModel profile)
            return;

        // A scavenge scan reads through the profile's DbxStore/DbxEmailFile in the
        // background -- cancel it before disposing that store below, so the scan sees a
        // clean OperationCanceledException instead of an ObjectDisposedException from
        // whatever it happens to be reading mid-scan. This is checked against
        // _scavengeProfile (the profile the in-flight scan actually owns), not
        // _selectedProfile: navigating to a different profile's tree while a scan runs in
        // the background is allowed, so the scavenged profile and the selected profile can
        // be two different profiles by the time either one's "x" is clicked.
        if (ReferenceEquals(_scavengeProfile, profile))
        {
            _scavengeCts?.Cancel();
        }

        if (ReferenceEquals(_selectedProfile, profile))
        {
            _selectedProfile = null;
            _selectedNode = null;
            _selectedTreeView = null;

            ScavengeButton.Visibility = Visibility.Collapsed;
            CancelScavengeButton.Visibility = Visibility.Collapsed;
            RecoverableBar.Visibility = Visibility.Collapsed;

            // Design §4/§6: closing the active profile returns list/reader/scavenge to the
            // "no profile open" visual state. MessageList/MessageReader/CurrentFolderScavengeStatus
            // are cleared by _viewModel.CloseProfile below (bound, so the list clears itself),
            // but ReaderWebView isn't data-bound -- it keeps showing the last rendered message
            // body unless we explicitly navigate it away.
            NavigateToRenderedMessage(string.Empty);
            ImagesBlockedBar.Visibility = Visibility.Collapsed;
        }

        _viewModel.CloseProfile(profile);
    }

    private async void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        try
        {
            if (sender is TreeView { DataContext: ProfileViewModel profile } treeView && e.NewValue is FolderNodeViewModel node)
            {
                // Each TreeView keeps its own SelectedItem, so WPF only raises this event when
                // a tree's own selection changes -- re-clicking an already-selected node in a
                // DIFFERENT tree never fires here at all (WPF short-circuits it). Clearing the
                // previously-selected tree's selection when the user moves to a different tree
                // means that tree's node is no longer "selected" from WPF's point of view, so
                // clicking back on it later raises this event again instead of being a dead
                // click. This recurses synchronously into this handler with e.NewValue == null
                // for the old tree, which the check below already no-ops on.
                if (_selectedTreeView is { } previousTree && !ReferenceEquals(previousTree, treeView)
                    && _selectedNode is { } previousNode
                    && FindContainer(previousTree, previousNode) is { } previousContainer)
                {
                    previousContainer.IsSelected = false;
                }

                _selectedProfile = profile;
                _selectedNode = node;
                _selectedTreeView = treeView;
                await _viewModel.SelectFolderAsync(profile, node);
                UpdateScavengeButtonVisibility(node);
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível carregar esta pasta: {ex.Message}";
        }
    }

    // Recursively looks up the TreeViewItem container for a view-model item, descending
    // through already-generated child containers. FolderNodeViewModel has no parent
    // reference to walk up from, so this walks down instead; it only needs to succeed for
    // items that were previously selected, whose ancestor containers must already exist
    // (a user can't click a node without its ancestors being expanded and realized first).
    private static TreeViewItem? FindContainer(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem direct)
            return direct;

        foreach (var childItem in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(childItem) is TreeViewItem childContainer
                && FindContainer(childContainer, item) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void NewSubfolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not FolderNodeViewModel node)
            return;
        if ((sender as MenuItem)?.Parent is not ContextMenu { PlacementTarget: FrameworkElement placementTarget })
            return;
        if (FindAncestor<TreeView>(placementTarget) is not { DataContext: ProfileViewModel profile } treeView)
            return;

        _ = CreateFolderAsync(profile, node.Folder.Id, treeView);
    }

    private void NewTopLevelFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Parent is not ContextMenu { PlacementTarget: TreeView { DataContext: ProfileViewModel profile } treeView })
            return;

        _ = CreateFolderAsync(profile, parentId: null, treeView);
    }

    private async Task CreateFolderAsync(ProfileViewModel profile, int? parentId, TreeView treeView)
    {
        var dialog = new NewFolderDialog { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        // DbxStore.CreateFolderAsync mutates its internal folder list on a background
        // thread (Task.Run) without locking. Disabling this tree for the duration closes
        // the one realistic way a concurrent read could happen: the user clicking a
        // different node in the SAME tree while this create is in flight.
        treeView.IsEnabled = false;
        try
        {
            await profile.CreateFolderAsync(dialog.FolderName, parentId);

            // FolderTree.Build (inside profile.CreateFolderAsync) just replaced every node
            // in this tree with a brand-new instance — any node this window had cached as
            // "selected" for this profile is now detached from every tree. Reset the same
            // selection state CloseProfile_Click resets when a profile closes, but without
            // touching _selectedProfile itself (the profile is still open).
            if (ReferenceEquals(_selectedProfile, profile))
            {
                _selectedNode = null;
                _selectedTreeView = null;
                ScavengeButton.Visibility = Visibility.Collapsed;
                RecoverableBar.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LEC — Não foi possível criar a pasta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            treeView.IsEnabled = true;
        }
    }

    private void DeleteFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not FolderNodeViewModel node)
            return;
        if ((sender as MenuItem)?.Parent is not ContextMenu { PlacementTarget: FrameworkElement placementTarget })
            return;
        if (FindAncestor<TreeView>(placementTarget) is not { DataContext: ProfileViewModel profile } treeView)
            return;

        _ = ConfirmAndDeleteFolderAsync(profile, node, treeView);
    }

    private async Task ConfirmAndDeleteFolderAsync(ProfileViewModel profile, FolderNodeViewModel node, TreeView treeView)
    {
        var result = MessageBox.Show(this,
            $"Apagar a pasta \"{node.DisplayName}\" e tudo dentro dela (subpastas e mensagens)?",
            "LEC — Apagar pasta", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        // The old tree (still in memory, pre-rebuild) is what tells us whether the current
        // selection lived inside the subtree about to disappear -- profile.DeleteFolderAsync
        // below replaces every node in profile.FolderTree with brand-new instances, so this
        // check has to happen against the node we already have, not anything read afterward.
        bool selectionIsInsideDeletedSubtree = ReferenceEquals(_selectedProfile, profile)
            && _selectedNode is { } current
            && (ReferenceEquals(current, node) || IsDescendantOf(node, current));

        treeView.IsEnabled = false;
        try
        {
            await profile.DeleteFolderAsync(node.Folder.Id);

            if (selectionIsInsideDeletedSubtree)
            {
                _selectedNode = null;
                _selectedTreeView = null;
                ScavengeButton.Visibility = Visibility.Collapsed;
                RecoverableBar.Visibility = Visibility.Collapsed;
                NavigateToRenderedMessage(string.Empty);
                ImagesBlockedBar.Visibility = Visibility.Collapsed;
                _viewModel.ClearCurrentFolder();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LEC — Não foi possível apagar a pasta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            treeView.IsEnabled = true;
        }
    }

    // True if `candidate` is `ancestor` itself or appears anywhere in its subtree. Used to
    // decide whether the currently-selected folder is about to be hidden by a delete two
    // levels up (e.g. selection is a grandchild of the folder the user right-clicked).
    private static bool IsDescendantOf(FolderNodeViewModel ancestor, FolderNodeViewModel candidate)
    {
        foreach (var child in ancestor.Children)
        {
            if (ReferenceEquals(child, candidate) || IsDescendantOf(child, candidate))
                return true;
        }
        return false;
    }

    private async void MoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is not FolderNodeViewModel node)
            return;
        if ((sender as MenuItem)?.Parent is not ContextMenu { PlacementTarget: FrameworkElement placementTarget })
            return;
        if (FindAncestor<TreeView>(placementTarget) is not { DataContext: ProfileViewModel profile } treeView)
            return;

        try
        {
            var dialog = new MoveFolderDialog(_viewModel.OpenProfiles, profile, node) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedProfile is not { } destinationProfile)
                return;

            treeView.IsEnabled = false;
            try
            {
                await _viewModel.MoveFolderAsync(profile, node, destinationProfile, dialog.SelectedFolderId);
            }
            finally
            {
                treeView.IsEnabled = true;
            }

            // A cross-profile move rebuilds BOTH profiles' FolderTrees (the source's via
            // DeleteFolderAsync, the destination's via CreateFolderAsync) -- reset unconditionally
            // whenever the currently displayed profile is either one, not just the source, since
            // a same-profile move already rebuilds only that one tree either way.
            if (ReferenceEquals(_selectedProfile, profile) || ReferenceEquals(_selectedProfile, destinationProfile))
            {
                _selectedNode = null;
                _selectedTreeView = null;
                ScavengeButton.Visibility = Visibility.Collapsed;
                RecoverableBar.Visibility = Visibility.Collapsed;
                NavigateToRenderedMessage(string.Empty);
                ImagesBlockedBar.Visibility = Visibility.Collapsed;
                _viewModel.ClearCurrentFolder();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "LEC — Não foi possível mover a pasta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Percorre a árvore visual a partir de `start` até achar um ancestral do tipo T --
    // necessário porque um ContextMenu não está na mesma árvore visual do elemento que
    // abriu ele (é uma Popup separada); PlacementTarget é o único elo de volta.
    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }
        return null;
    }

    private void UpdateScavengeButtonVisibility(FolderNodeViewModel node)
    {
        var status = _viewModel.CurrentFolderScavengeStatus;

        // Only one scan can run at a time (see ScavengeButton_Click) -- while _scavengeCts is
        // set, a scan is in flight for whichever (profile, folder) it was started against, so
        // the trigger must stay hidden everywhere, including trees/folders the user switches
        // to mid-scan. Otherwise clicking it here would start a second concurrent scan that
        // stomps the first one's cancellation token and status.
        bool showTriggerButton = _scavengeCts is null
            && node.Folder.IsLoadable
            && _viewModel.MessageList is not null // a profile is open
            && status is null or { State: ScavengeState.NeverScanned }
            && HasPartialHealth(node);
        ScavengeButton.Visibility = showTriggerButton ? Visibility.Visible : Visibility.Collapsed;

        bool showBar = status is { State: ScavengeState.Scanned, RecoveredCount: > 0 };
        RecoverableBar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        if (showBar)
        {
            RecoverableCountText.Text = $"{status!.RecoveredCount} mensagens recuperáveis neste arquivo";
            ToggleRecoverableButton.Content = status.IsVisible ? "Esconder" : "Mostrar";
        }
    }

    private bool HasPartialHealth(FolderNodeViewModel node) =>
        _viewModel.Store?.GetFolderHealth(node.Folder) == StoreHealth.Partial;

    private async void ToggleRecoverableButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is not { } profile || _selectedNode is not { } node)
            return;

        try
        {
            bool newVisibility = !(_viewModel.CurrentFolderScavengeStatus?.IsVisible ?? false);
            profile.Store.SetScavengeVisibility(node.Folder, newVisibility);
            await _viewModel.SelectFolderAsync(profile, node); // reload the list + refresh status/button state
            UpdateScavengeButtonVisibility(node);
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível alternar a exibição de recuperáveis: {ex.Message}";
        }
    }

    private async void ScavengeButton_Click(object sender, RoutedEventArgs e)
    {
        // Only one scan at a time: _scavengeCts, ScavengeButton and RecoverableBar are all
        // single shared fields/controls even though there are now N profile trees. Without
        // this guard, clicking the (now-stale) trigger for a different profile's folder while
        // a scan is already running would overwrite _scavengeCts with a second CTS -- the
        // first scan becomes uncancellable, whichever scan finishes last disposes the other's
        // token source, and CurrentFolderScavengeStatus/RecoverableBar end up showing whichever
        // scan happened to finish last regardless of which folder is on screen.
        if (_scavengeCts is not null)
            return;

        if (_selectedProfile is not { } profile || _selectedNode is not { } node)
            return;

        // Snapshot which (profile, folder) this scan belongs to, distinct from
        // _selectedProfile/_selectedNode which can move on while the scan is still running.
        var scanProfile = profile;
        var scanNode = node;

        _scavengeCts = new CancellationTokenSource();
        _scavengeProfile = scanProfile;
        try
        {
            ScavengeButton.Visibility = Visibility.Collapsed;
            CancelScavengeButton.Visibility = Visibility.Visible;

            var progress = new Progress<double>(p => _viewModel.StatusText = $"Varrendo pasta... {p:P0}");
            await _viewModel.RunScavengeAsync(profile, node, progress, _scavengeCts.Token);

            _viewModel.StatusText = null;

            // If the user has since selected a different folder/profile, that selection's
            // button state was already correctly computed (and kept collapsed) by
            // UpdateScavengeButtonVisibility while this scan was in flight -- refreshing it
            // now against the possibly-stale `node` parameter would show the wrong folder's
            // scan-trigger state.
            if (ReferenceEquals(_selectedProfile, scanProfile) && ReferenceEquals(_selectedNode, scanNode))
                UpdateScavengeButtonVisibility(node);
        }
        catch (OperationCanceledException)
        {
            _viewModel.StatusText = "Varredura cancelada.";
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível varrer esta pasta: {ex.Message}";
        }
        finally
        {
            CancelScavengeButton.Visibility = Visibility.Collapsed;
            _scavengeCts?.Dispose();
            _scavengeCts = null;
            _scavengeProfile = null;
        }
    }

    private void CancelScavengeButton_Click(object sender, RoutedEventArgs e)
    {
        _scavengeCts?.Cancel();
    }

    private async void MessageListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (MessageListView.SelectedItems.Count == 1 && MessageListView.SelectedItem is MessageHeader header)
            {
                if (_imageBlocker is not null)
                {
                    _imageBlocker.BlockImages = true;
                    ImagesBlockedBar.Visibility = Visibility.Visible;
                }

                await _viewModel.SelectMessageAsync(header);
                if (_viewModel.MessageReader?.Current is { } rendered)
                    NavigateToRenderedMessage(rendered.Html);
            }
            else
            {
                // 0 selected, or a batch selection (2+) -- no single message to show.
                NavigateToRenderedMessage(string.Empty);
                ImagesBlockedBar.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível exibir esta mensagem: {ex.Message}";
        }
    }

    private void MessageListView_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: not null } header) return;
        if (_viewModel.MessageList is not { } messageList) return; // no profile open

        if (header.Column != SenderColumn && header.Column != DateColumn) return;

        // ApplySortColumn (in MessageListViewModel) does Messages.Clear() then re-adds in the
        // new order; Clear() raises a CollectionChanged Reset which drops SelectedItems and
        // blanks the reading pane via MessageListView_SelectionChanged. Capture the selection
        // here and restore it after the sort so the reorder is transparent to the user.
        var selected = MessageListView.SelectedItems.Cast<MessageHeader>().ToList();

        if (header.Column == SenderColumn)
            messageList.SortBySender();
        else
            messageList.SortByDate();

        foreach (var h in selected)
            MessageListView.SelectedItems.Add(h);
    }

    // Resolves which messages a context-menu action or the Delete key should act on: the
    // current selection if there is one -- even if the row that triggered the menu isn't
    // part of it, right-clicking off-selection intentionally leaves the existing selection
    // alone -- or the clicked row itself as a fallback for the very first right-click in a
    // freshly loaded list, before anything has ever been selected.
    private List<MessageHeader> ResolveTargetMessages(object sender)
    {
        if (MessageListView.SelectedItems.Count > 0)
            return MessageListView.SelectedItems.Cast<MessageHeader>().ToList();

        if ((sender as MenuItem)?.DataContext is MessageHeader header)
            return new List<MessageHeader> { header };

        return new List<MessageHeader>();
    }

    private async void MoveMessage_Click(object sender, RoutedEventArgs e)
    {
        var messages = ResolveTargetMessages(sender);
        if (messages.Count == 0)
            return;
        if (_selectedProfile is not { } sourceProfile)
            return;

        var dialog = new MoveMessageDialog(_viewModel.OpenProfiles, sourceProfile, messages[0].Id.FolderId) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedProfile is not { } destinationProfile || dialog.SelectedFolderId is not { } destinationFolderId)
            return;

        Exception? failure = null;
        try
        {
            await _viewModel.MoveMessagesAsync(sourceProfile, messages, destinationProfile, destinationFolderId);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // The moved messages -- all of them on success, or whichever ones completed before a
        // partial failure -- are now hidden from the folder currently on screen (its source),
        // so reload that folder's list unconditionally so they disappear, and clear the
        // reading pane in case it was showing one of them (same visual reset
        // CloseProfile_Click already uses for "nothing selected"). This must run even when
        // the batch above threw partway through: skipping it would leave already-moved
        // messages showing as stale rows, and retrying the same selection would move (i.e.
        // duplicate) them again.
        if (_selectedProfile is { } profile && _selectedNode is { } node)
            await _viewModel.SelectFolderAsync(profile, node);

        NavigateToRenderedMessage(string.Empty);
        ImagesBlockedBar.Visibility = Visibility.Collapsed;

        if (failure is not null)
            MessageBox.Show(this, failure.Message, "LEC — Não foi possível mover a mensagem", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void DeleteMessage_Click(object sender, RoutedEventArgs e)
    {
        var messages = ResolveTargetMessages(sender);
        if (messages.Count == 0)
            return;

        await ConfirmAndDeleteAsync(messages);
    }

    private async void MessageListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;
        if (MessageListView.SelectedItems.Count == 0)
            return;

        e.Handled = true;
        await ConfirmAndDeleteAsync(MessageListView.SelectedItems.Cast<MessageHeader>().ToList());
    }

    private async Task ConfirmAndDeleteAsync(List<MessageHeader> messages)
    {
        if (_selectedProfile is not { } sourceProfile)
            return;

        string question = messages.Count == 1
            ? "Apagar esta mensagem?"
            : $"Apagar estas {messages.Count} mensagens?";
        var result = MessageBox.Show(this, question, "LEC — Apagar mensagem",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        Exception? failure = null;
        try
        {
            await _viewModel.DeleteMessagesAsync(sourceProfile, messages);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // The deleted messages -- all of them on success, or whichever ones completed before
        // a partial failure -- are now hidden from the folder currently on screen, so reload
        // that folder's list unconditionally so they disappear, and clear the reading pane in
        // case it was showing one of them (same visual reset MoveMessage_Click/
        // CloseProfile_Click already use for "nothing selected"). Re-read both
        // _selectedProfile/_selectedNode together here, exactly as MoveMessage_Click does,
        // rather than pairing a pre-await profile with a post-await node -- the user's
        // selection could in principle have moved on during the confirmation dialog and the
        // delete call. This must run even when the batch above threw partway through: skipping
        // it would leave already-deleted messages showing as stale rows on screen.
        if (_selectedProfile is { } profile && _selectedNode is { } node)
            await _viewModel.SelectFolderAsync(profile, node);

        NavigateToRenderedMessage(string.Empty);
        ImagesBlockedBar.Visibility = Visibility.Collapsed;

        if (failure is not null)
            MessageBox.Show(this, failure.Message, "LEC — Não foi possível apagar a mensagem", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void NavigateToRenderedMessage(string html)
    {
        // Second, independent cap in front of NavigateToString -- see MaxNavigateHtmlChars.
        string toNavigate = html;
        if (html.Length > MaxNavigateHtmlChars)
        {
            toNavigate = html[..MaxNavigateHtmlChars]
                + $"\n\n[conteúdo truncado para exibição — {html.Length:N0} caracteres no total]";
        }

        try
        {
            ReaderWebView.NavigateToString(toNavigate);
        }
        catch (Exception ex)
        {
            // Final backstop: NavigateToString itself has an internal ~2 MB limit that our
            // char-count cap only approximates (multi-byte content can trip it earlier).
            _viewModel.StatusText = $"Não foi possível exibir esta mensagem: {ex.Message}";
        }
    }
}
