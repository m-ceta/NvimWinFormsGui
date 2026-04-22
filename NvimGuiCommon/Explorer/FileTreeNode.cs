using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace NvimGuiCommon.Explorer;

public partial class FileTreeNode : ObservableObject
{
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string fullPath = string.Empty;
    [ObservableProperty] private bool isDirectory;
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool isSelected;

    public ObservableCollection<FileTreeNode> Children { get; } = new();
    public override string ToString() => Name;
}
