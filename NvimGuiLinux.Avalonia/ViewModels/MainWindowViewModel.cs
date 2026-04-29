using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NvimGuiCommon.Config;
using NvimGuiCommon.Diagnostics;
using NvimGuiCommon.Editor;
using NvimGuiCommon.Explorer;

namespace NvimGuiLinux.Avalonia.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly FileTreeService _treeService = new();
    private readonly string[] _args;

    [ObservableProperty] private ObservableCollection<FileTreeNode> roots = new();
    [ObservableProperty] private FileTreeNode? selectedNode;
    [ObservableProperty] private string rootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] private OpenMode openMode = OpenMode.NewTab;
    [ObservableProperty] private bool isFolderTreeVisible = true;
    [ObservableProperty] private double folderTreeWidth = 280;
    [ObservableProperty] private string statusText = "Ready";
    [ObservableProperty] private string fontFamilyName = "DejaVu Sans Mono, Noto Sans Mono, Noto Sans Mono CJK JP, monospace";
    [ObservableProperty] private double fontSize = 14;
    [ObservableProperty] private double lineHeight = 1.0;
    [ObservableProperty] private bool useFixedCellMetrics = false;
    [ObservableProperty] private double fixedCellWidth = 8;
    [ObservableProperty] private double fixedCellHeight = 18;

    public MainWindowViewModel(string[] args)
    {
        _args = args;
        Editor = new EditorController();
        Editor.Stderr += s => StatusText = s.Trim();
        ReloadTree();
    }

    public EditorController Editor { get; }
    public LineGridModel Grid => Editor.Grid;

    public async Task InitializeAsync()
    {
        await Editor.StartAsync();
        var parsed = AppArgs.Parse(_args);
        if (!string.IsNullOrWhiteSpace(parsed.FilePath))
            await Editor.OpenFileAsync(Path.GetFullPath(parsed.FilePath), OpenMode.Current);
    }

    public void ReloadTree()
    {
        Roots = _treeService.LoadRoot(RootPath);
        if (Roots.Count > 0)
            EnsureDirectoryLoaded(Roots[0]);
        GuiLogger.Info(GuiLogCategory.FolderTree, () => $"FolderTree root loaded path={RootPath}");
    }

    public void EnsureDirectoryLoaded(FileTreeNode? node)
    {
        if (node?.IsDirectory != true)
            return;

        if (node.Children.Count == 1 && node.Children[0].IsPlaceholder)
            _treeService.Populate(node);
    }

    public void ToggleFolderTree()
    {
        IsFolderTreeVisible = !IsFolderTreeVisible;
        GuiLogger.Info(GuiLogCategory.FolderTree, () => $"FolderTree visible={IsFolderTreeVisible}");
    }

    public async Task OpenSelectedAsync(OpenMode? modeOverride = null)
    {
        if (SelectedNode is null || SelectedNode.IsDirectory)
            return;

        var mode = modeOverride ?? OpenMode;
        GuiLogger.Info(GuiLogCategory.FolderTree, () => $"FolderTree file open path={SelectedNode.FullPath} mode={mode}");
        await Editor.OpenFileAsync(SelectedNode.FullPath, mode);
    }

    public async Task CompareSelectedWithCurrentAsync()
    {
        if (SelectedNode is null || SelectedNode.IsDirectory)
            return;

        GuiLogger.Info(GuiLogCategory.FolderTree, () => $"FolderTree compare path={SelectedNode.FullPath}");
        await Editor.DiffSplitAsync(SelectedNode.FullPath);
    }

    public void ReloadSelectedNode()
    {
        if (SelectedNode?.IsDirectory == true)
        {
            _treeService.Populate(SelectedNode);
            GuiLogger.Info(GuiLogCategory.FolderTree, () => $"FolderTree node reloaded path={SelectedNode.FullPath}");
            return;
        }

        ReloadTree();
    }

    [RelayCommand] private void Save() => _ = Editor.SaveAsync();
    [RelayCommand] private void CloseBuffer() => _ = Editor.CloseBufferAsync();
}
