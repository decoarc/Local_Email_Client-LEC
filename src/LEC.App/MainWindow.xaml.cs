// src/LEC.App/MainWindow.xaml.cs
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

        if (ReferenceEquals(_selectedProfile, profile))
        {
            _selectedProfile = null;
            _selectedNode = null;
            _selectedTreeView = null;

            // A scavenge scan reads through the profile's DbxStore/DbxEmailFile in the
            // background -- cancel it before disposing that store below, so the scan sees a
            // clean OperationCanceledException instead of an ObjectDisposedException from
            // whatever it happens to be reading mid-scan.
            _scavengeCts?.Cancel();

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
            if (MessageListView.SelectedItem is MessageHeader header)
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
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"Não foi possível exibir esta mensagem: {ex.Message}";
        }
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
