using System.Collections.ObjectModel;

namespace NvimGuiCommon.Explorer;

public sealed class FileTreeService
{
    public ObservableCollection<FileTreeNode> LoadRoot(string rootPath)
    {
        var list = new ObservableCollection<FileTreeNode>();
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return list;
        var root = CreateNode(rootPath, true);
        Populate(root);
        list.Add(root);
        return list;
    }

    public void Populate(FileTreeNode node)
    {
        if (!node.IsDirectory || !Directory.Exists(node.FullPath)) return;
        node.Children.Clear();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(node.FullPath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                node.Children.Add(CreateNode(dir, true));
            foreach (var file in Directory.EnumerateFiles(node.FullPath).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                node.Children.Add(CreateNode(file, false));
        }
        catch { }
    }

    private static FileTreeNode CreateNode(string fullPath, bool isDirectory)
        => new FileTreeNode
        {
            Name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : fullPath,
            FullPath = fullPath,
            IsDirectory = isDirectory,
        };
}
