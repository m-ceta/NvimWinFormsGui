using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using NvimGuiCommon.Diagnostics;
using NvimGuiCommon.Editor;
using NvimGuiCommon.Explorer;
using NvimGuiLinux.Avalonia.Controls;
using NvimGuiLinux.Avalonia.ViewModels;

namespace NvimGuiLinux.Avalonia.Views;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _layoutLogCts;
    private FileTreeNode? _contextTargetNode;

    public MainWindow()
    {
        InitializeComponent();
        Opened += MainWindow_Opened;
        LayoutUpdated += (_, _) => ScheduleLayoutLog();
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        try
        {
            if (DataContext is not MainWindowViewModel vm)
                return;

            GuiLogger.Info(GuiLogCategory.Performance, () => "MainWindow opened begin");
            EditorGrid.Bind(vm.Editor, vm.Grid);
            FloatingGridLayer.Bind(vm.Grid);
            PopupMenuLayer.Bind(vm.Grid);
            CmdlineLayer.Bind(vm.Grid);
            MessageLayer.Bind(vm.Grid);
            GuiLogger.Info(GuiLogCategory.Performance, () => "MainWindow controls bound");

            await vm.InitializeAsync();
            GuiLogger.Info(GuiLogCategory.Performance, () => "MainWindow initialize completed");
            ApplyFolderTreeLayout();
            UpdateOpenModeChecks();
            await EditorGrid.ResizeNvimToBoundsAsync();
            GuiLogger.Info(GuiLogCategory.Performance, () => "MainWindow initial resize completed");
            await RestoreEditorFocusAsync("window opened");
        }
        catch (Exception ex)
        {
            GuiLogger.Error(GuiLogCategory.Performance, () => $"MainWindow opened failed error={ex}");
        }
    }

    private async Task RestoreEditorFocusAsync(string reason)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await Task.Delay(25);
            EditorGrid.Focus();
            GuiLogger.Info(GuiLogCategory.Focus, () => $"focus restored reason={reason}");
        });
    }

    private void ApplyFolderTreeLayout()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var treeWidth = vm.IsFolderTreeVisible ? Math.Max(220, vm.FolderTreeWidth) : 0;
        LayoutRoot.ColumnDefinitions[0].Width = new GridLength(treeWidth, GridUnitType.Pixel);
        LayoutRoot.ColumnDefinitions[1].Width = new GridLength(vm.IsFolderTreeVisible ? 4 : 0, GridUnitType.Pixel);
        FolderTreePane.IsVisible = vm.IsFolderTreeVisible;
        FolderTreeSplitter.IsVisible = vm.IsFolderTreeVisible;
        GuiLogger.Info(GuiLogCategory.Layout, () => $"FolderTreeLayout visible={vm.IsFolderTreeVisible} width={treeWidth}");
        ScheduleLayoutLog();
    }

    private void ScheduleLayoutLog()
    {
        _layoutLogCts?.Cancel();
        _layoutLogCts?.Dispose();
        _layoutLogCts = new CancellationTokenSource();
        var token = _layoutLogCts.Token;
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(80, token);
                if (token.IsCancellationRequested)
                    return;
                GuiLogger.Info(
                    GuiLogCategory.Layout,
                    () => $"EditorArea={FormatRect(EditorArea.Bounds)} FolderTreePane={FormatRect(FolderTreePane.Bounds)} Splitter={FormatRect(FolderTreeSplitter.Bounds)} LineGridControl={FormatRect(EditorGrid.Bounds)} CmdlineLayer={FormatRect(CmdlineLayer.Bounds)} MessageLayer={FormatRect(MessageLayer.Bounds)}");
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void TreeView_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.SelectedNode = e.AddedItems.OfType<FileTreeNode>().FirstOrDefault();
        vm.EnsureDirectoryLoaded(vm.SelectedNode);
        if (vm.SelectedNode is not null)
            GuiLogger.Info(GuiLogCategory.FolderTree, () => $"node selected path={vm.SelectedNode.FullPath} dir={vm.SelectedNode.IsDirectory}");
    }

    private async void TreeNode_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not Control control || control.DataContext is not FileTreeNode node)
            return;

        vm.SelectedNode = node;
        GuiLogger.Info(GuiLogCategory.FolderTree, () => $"node double clicked path={node.FullPath} dir={node.IsDirectory}");
        if (node.IsDirectory)
        {
            ToggleNodeExpansion(node);
            return;
        }

        await vm.OpenSelectedAsync(OpenMode.NewTab);
        await RestoreEditorFocusAsync("folder tree double click");
    }

    private async void FolderTree_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || e.Key != Key.Enter || vm.SelectedNode is null)
            return;

        e.Handled = true;
        if (vm.SelectedNode.IsDirectory)
        {
            ToggleNodeExpansion(vm.SelectedNode);
            return;
        }

        GuiLogger.Info(GuiLogCategory.FolderTree, () => $"enter key open path={vm.SelectedNode.FullPath}");
        await vm.OpenSelectedAsync();
        await RestoreEditorFocusAsync("folder tree enter");
    }

    private void FolderTree_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        _contextTargetNode = ResolveNode(e.Source);
        if (_contextTargetNode is not null)
            vm.SelectedNode = _contextTargetNode;
    }

    private void FolderTreeContextMenu_OnOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        menu.Items.Clear();
        var node = _contextTargetNode;
        var targetPath = node?.FullPath ?? "<blank>";
        GuiLogger.Info(GuiLogCategory.ContextMenu, () => $"context menu opening target path={targetPath}");

        menu.Items.Add(CreateContextMenuItem("ツリーの更新(&U)...", node is null || node.IsDirectory, async () => await ChangeTreeRootAsync(), "change root"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateContextMenuItem("新しいタブで開く", node is not null, async () => await OpenContextNodeAsync(OpenMode.NewTab), node is null ? "blank target" : "open selected"));
        menu.Items.Add(CreateContextMenuItem("現在の画面で開く", node is not null, async () => await OpenContextNodeAsync(OpenMode.Current), node is null ? "blank target" : "open selected"));
        menu.Items.Add(CreateContextMenuItem("垂直分割で開く", node is not null, async () => await OpenContextNodeAsync(OpenMode.VSplit), node is null ? "blank target" : "open selected"));
        menu.Items.Add(CreateContextMenuItem("水平分割で開く", node is not null, async () => await OpenContextNodeAsync(OpenMode.Split), node is null ? "blank target" : "open selected"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateContextMenuItem("現在のバッファと比較", node is not null && !node.IsDirectory, async () => await CompareContextNodeAsync(), node is null ? "blank target" : node.IsDirectory ? "directory node" : "enabled"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateContextMenuItem("再読み込み", node is not null, async () => await ReloadContextNodeAsync(), node is null ? "blank target" : "enabled"));
    }

    private async void FolderTreeContextMenu_OnClosed(object? sender, RoutedEventArgs e)
    {
        await RestoreEditorFocusAsync("context menu closed");
    }

    private MenuItem CreateContextMenuItem(string header, bool enabled, Func<Task> action, string reason)
    {
        GuiLogger.Info(GuiLogCategory.ContextMenu, () => $"context command {header} enabled={enabled} reason={reason}");
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += async (_, _) =>
        {
            GuiLogger.Info(GuiLogCategory.ContextMenu, () => $"context command clicked header={header}");
            await action();
            await RestoreEditorFocusAsync($"context menu command {header}");
        };
        return item;
    }

    private void MainMenuItem_OnSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item)
            GuiLogger.Info(GuiLogCategory.MainMenu, () => $"menu opened header={item.Header}");
    }

    private async void MenuOpen_OnClick(object? sender, RoutedEventArgs e)
    {
        GuiLogger.Info(GuiLogCategory.MainMenu, () => "command clicked header=開く");
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "開く",
            AllowMultiple = false
        });
        var file = files.FirstOrDefault();
        if (file is not null && DataContext is MainWindowViewModel vm)
            await vm.Editor.OpenFileAsync(file.Path.LocalPath, vm.OpenMode);
        await RestoreEditorFocusAsync("menu open");
    }

    private async void MenuSave_OnClick(object? sender, RoutedEventArgs e)
    {
        GuiLogger.Info(GuiLogCategory.MainMenu, () => "command clicked header=保存");
        if (DataContext is MainWindowViewModel vm)
            await vm.Editor.SaveAsync();
        await RestoreEditorFocusAsync("menu save");
    }

    private async void MenuSaveAs_OnClick(object? sender, RoutedEventArgs e)
    {
        GuiLogger.Info(GuiLogCategory.MainMenu, () => "command clicked header=名前を付けて保存");
        if (DataContext is not MainWindowViewModel vm)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "名前を付けて保存"
        });
        if (file is not null)
            await vm.Editor.SaveAsAsync(file.Path.LocalPath);
        await RestoreEditorFocusAsync("menu save as");
    }

    private async void MenuCloseBuffer_OnClick(object? sender, RoutedEventArgs e)
    {
        GuiLogger.Info(GuiLogCategory.MainMenu, () => "command clicked header=閉じる");
        if (DataContext is MainWindowViewModel vm)
            await vm.Editor.CloseBufferAsync();
        await RestoreEditorFocusAsync("menu close buffer");
    }

    private void MenuExit_OnClick(object? sender, RoutedEventArgs e)
    {
        GuiLogger.Info(GuiLogCategory.MainMenu, () => "command clicked header=終了");
        Close();
    }

    private void OpenModeNewTab_OnClick(object? sender, RoutedEventArgs e) => SetOpenMode(OpenMode.NewTab);
    private void OpenModeCurrent_OnClick(object? sender, RoutedEventArgs e) => SetOpenMode(OpenMode.Current);
    private void OpenModeVSplit_OnClick(object? sender, RoutedEventArgs e) => SetOpenMode(OpenMode.VSplit);
    private void OpenModeSplit_OnClick(object? sender, RoutedEventArgs e) => SetOpenMode(OpenMode.Split);

    private void SetOpenMode(OpenMode mode)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.OpenMode = mode;
        UpdateOpenModeChecks();
        GuiLogger.Info(GuiLogCategory.MainMenu, () => $"command clicked header=開き方 mode={mode}");
    }

    private void UpdateOpenModeChecks()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        OpenModeNewTabMenuItem.Header = FormatOpenModeHeader("新しいタブ", vm.OpenMode == OpenMode.NewTab);
        OpenModeCurrentMenuItem.Header = FormatOpenModeHeader("現在の画面", vm.OpenMode == OpenMode.Current);
        OpenModeVSplitMenuItem.Header = FormatOpenModeHeader("垂直分割", vm.OpenMode == OpenMode.VSplit);
        OpenModeSplitMenuItem.Header = FormatOpenModeHeader("水平分割", vm.OpenMode == OpenMode.Split);
    }

    private async void ToggleFolderTree_OnClick(object? sender, RoutedEventArgs e)
    {
        GuiLogger.Info(GuiLogCategory.MainMenu, () => "command clicked header=フォルダツリー");
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ToggleFolderTree();
            ApplyFolderTreeLayout();
        }
        await RestoreEditorFocusAsync("toggle folder tree");
    }

    private async void ChangeTreeRoot_OnClick(object? sender, RoutedEventArgs e)
    {
        GuiLogger.Info(GuiLogCategory.MainMenu, () => "command clicked header=ツリーの更新");
        await ChangeTreeRootAsync();
        await RestoreEditorFocusAsync("change tree root");
    }

    private async Task ChangeTreeRootAsync()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "ツリーのルートフォルダを選択",
            AllowMultiple = false
        });
        var folder = folders.FirstOrDefault();
        if (folder is null)
            return;

        vm.RootPath = folder.Path.LocalPath;
        vm.ReloadTree();
    }

    private async void ReloadTree_OnClick(object? sender, RoutedEventArgs e)
    {
        GuiLogger.Info(GuiLogCategory.MainMenu, () => "command clicked header=ツリー再読み込み");
        if (DataContext is MainWindowViewModel vm)
            vm.ReloadTree();
        await RestoreEditorFocusAsync("reload tree");
    }

    private void FolderTreeSplitter_OnDragDelta(object? sender, VectorEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.IsFolderTreeVisible)
            return;

        vm.FolderTreeWidth = Math.Max(220, FolderTreePane.Bounds.Width);
        GuiLogger.Info(GuiLogCategory.Layout, () => $"splitter moved width={vm.FolderTreeWidth:F1}");
        ScheduleLayoutLog();
    }

    private async Task OpenContextNodeAsync(OpenMode mode)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.SelectedNode = _contextTargetNode;
        if (vm.SelectedNode?.IsDirectory == true)
        {
            ToggleNodeExpansion(vm.SelectedNode);
            return;
        }

        await vm.OpenSelectedAsync(mode);
    }

    private async Task CompareContextNodeAsync()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.SelectedNode = _contextTargetNode;
        await vm.CompareSelectedWithCurrentAsync();
    }

    private async Task ReloadContextNodeAsync()
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.SelectedNode = _contextTargetNode;
        vm.ReloadSelectedNode();
        await Task.CompletedTask;
    }

    private void ToggleNodeExpansion(FileTreeNode node)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        vm.EnsureDirectoryLoaded(node);
        if (FolderTreeView.ContainerFromItem(node) is TreeViewItem item)
        {
            item.IsExpanded = !item.IsExpanded;
            GuiLogger.Info(GuiLogCategory.FolderTree, () => $"node expanded path={node.FullPath} expanded={item.IsExpanded}");
        }
    }

    private static FileTreeNode? ResolveNode(object? source)
        => (source as global::Avalonia.Visual)?.FindAncestorOfType<TreeViewItem>()?.DataContext as FileTreeNode
           ?? (source as StyledElement)?.DataContext as FileTreeNode;

    private static string FormatRect(Rect rect)
        => $"x={rect.X:F1},y={rect.Y:F1},w={rect.Width:F1},h={rect.Height:F1}";

    private static string FormatOpenModeHeader(string label, bool selected)
        => selected ? $"* {label}" : label;
}
