using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NvimGuiCommon.Config;
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
    [ObservableProperty] private string statusText = "Ready";

    public MainWindowViewModel(string[] args)
    {
        _args = args;
        Editor = new EditorController();
        Editor.Stderr += s => StatusText = s.Trim();
        Roots = _treeService.LoadRoot(RootPath);
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

    public void PopulateSelectedDirectory()
    {
        if (SelectedNode?.IsDirectory == true)
            _treeService.Populate(SelectedNode);
    }

    [RelayCommand] private void ReloadTree() => Roots = _treeService.LoadRoot(RootPath);
    [RelayCommand] private async Task SaveAsync() => await Editor.SaveAsync();
    [RelayCommand] private async Task CloseBufferAsync() => await Editor.CloseBufferAsync();

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (SelectedNode is null || SelectedNode.IsDirectory) return;
        await Editor.OpenFileAsync(SelectedNode.FullPath, OpenMode);
    }

    [RelayCommand]
    private async Task CompareSelectedWithCurrentAsync()
    {
        if (SelectedNode is null || SelectedNode.IsDirectory) return;
        await Editor.DiffSplitAsync(SelectedNode.FullPath);
    }
}
