using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NvimGuiCommon.Config;
using NvimGuiCommon.Diagnostics;
using NvimGuiCommon.Editor;
using NvimGuiCommon.Explorer;
using NvimGuiCommon.Theme;

namespace NvimGuiLinux.Avalonia.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const string DefaultEditorFontFamily = "Sarasa Mono J, Noto Sans Mono CJK JP, Noto Sans Mono, BIZ UDGothic, MS Gothic, IPAGothic, VL Gothic, DejaVu Sans Mono, monospace";
    private readonly FileTreeService _treeService = new();
    private readonly string[] _args;

    [ObservableProperty] private ObservableCollection<FileTreeNode> roots = new();
    [ObservableProperty] private FileTreeNode? selectedNode;
    [ObservableProperty] private string rootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    [ObservableProperty] private OpenMode openMode = OpenMode.NewTab;
    [ObservableProperty] private bool isFolderTreeVisible = true;
    [ObservableProperty] private double folderTreeWidth = 280;
    [ObservableProperty] private string statusText = "Ready";
    [ObservableProperty] private string fontFamilyName = DefaultEditorFontFamily;
    [ObservableProperty] private double fontSize = 14;
    [ObservableProperty] private double lineHeight = 1.1;
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
        await ApplyNvimFontAsync();
        await Editor.StartAsync();
        var parsed = AppArgs.Parse(_args);
        if (!string.IsNullOrWhiteSpace(parsed.FilePath))
            await Editor.OpenFileAsync(Path.GetFullPath(parsed.FilePath), OpenMode.Current);
    }

    private async Task ApplyNvimFontAsync()
    {
        try
        {
            var font = await NvimFontProbe.TryGetFontAsync("nvim");
            if (font is null)
            {
                GuiLogger.Info(GuiLogCategory.Render, () => $"Font probe fallback font={FontFamilyName} size={FontSize:F1}");
                return;
            }

            FontFamilyName = ResolveInstalledFontFamilies(font.Value.familyCss);
            FontSize = font.Value.size;
            GuiLogger.Info(GuiLogCategory.Render, () => $"Font probe applied font={FontFamilyName} size={FontSize:F1}");
        }
        catch (Exception ex)
        {
            GuiLogger.Warn(GuiLogCategory.Render, () => $"Font probe failed error={ex.Message} fallback font={FontFamilyName} size={FontSize:F1}");
        }
    }

    private static string ResolveInstalledFontFamilies(string familyCss)
    {
        var installed = FontManager.Current.SystemFonts
            .Select(font => font.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requested = SplitFontFamilies(familyCss);
        string? coherentInstalled = null;
        string? firstInstalled = null;
        foreach (var family in requested)
        {
            var normalized = family.Trim();
            var unquoted = normalized.Trim().Trim('"');
            if (string.Equals(unquoted, "monospace", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!installed.Contains(unquoted))
                continue;

            firstInstalled ??= unquoted;
            if (SupportsLatinAndJapaneseInSameTypeface(unquoted))
            {
                coherentInstalled = unquoted;
                break;
            }
        }

        var selected = coherentInstalled ?? FindCoherentFallback(installed) ?? firstInstalled;
        if (string.IsNullOrWhiteSpace(selected))
            return familyCss;

        return selected!.IndexOf(' ') >= 0
            ? $"\"{selected}\", monospace"
            : $"{selected}, monospace";
    }

    private static string? FindCoherentFallback(HashSet<string> installed)
    {
        var candidates = new[]
        {
            "Sarasa Mono J",
            "BIZ UDGothic",
            "Noto Sans Mono CJK JP",
            "MS Gothic",
            "IPAGothic",
            "VL Gothic",
            "DejaVu Sans Mono"
        };

        foreach (var candidate in candidates)
        {
            if (installed.Contains(candidate) && SupportsLatinAndJapaneseInSameTypeface(candidate))
                return candidate;
        }

        return null;
    }

    private static bool SupportsLatinAndJapaneseInSameTypeface(string familyName)
    {
        var latin = TryGetMatchedTypefaceName(familyName, 'a');
        var japanese = TryGetMatchedTypefaceName(familyName, 0x3042);
        GuiLogger.Info(GuiLogCategory.Render, () => $"Font candidate family={familyName} latinMatch={latin ?? "<none>"} japaneseMatch={japanese ?? "<none>"}");
        return !string.IsNullOrWhiteSpace(latin)
            && !string.IsNullOrWhiteSpace(japanese)
            && string.Equals(latin, japanese, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetMatchedTypefaceName(string familyName, int codepoint)
    {
        var fontFamily = new FontFamily(familyName.IndexOf(' ') >= 0 ? $"\"{familyName}\"" : familyName);
        if (!FontManager.Current.TryMatchCharacter(
                codepoint,
                FontStyle.Normal,
                FontWeight.Normal,
                FontStretch.Normal,
                fontFamily,
                null,
                out var typeface))
        {
            return null;
        }

        return typeface.FontFamily.Name;
    }

    private static IReadOnlyList<string> SplitFontFamilies(string familyCss)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in familyCss)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                current.Append(ch);
                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                var item = current.ToString().Trim();
                if (item.Length > 0)
                    result.Add(item);
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        var last = current.ToString().Trim();
        if (last.Length > 0)
            result.Add(last);

        return result;
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
