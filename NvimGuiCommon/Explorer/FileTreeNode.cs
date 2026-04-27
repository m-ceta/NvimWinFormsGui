using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace NvimGuiCommon.Explorer;

public partial class FileTreeNode : ObservableObject
{
    private string _name = string.Empty;
    private string _fullPath = string.Empty;
    private bool _isDirectory;
    private bool _isExpanded;
    private bool _isSelected;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string FullPath
    {
        get => _fullPath;
        set => SetProperty(ref _fullPath, value);
    }

    public bool IsDirectory
    {
        get => _isDirectory;
        set => SetProperty(ref _isDirectory, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public ObservableCollection<FileTreeNode> Children { get; } = new();
    public override string ToString() => Name;
}
