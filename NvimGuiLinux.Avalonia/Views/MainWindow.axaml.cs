using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NvimGuiLinux.Avalonia.Controls;
using NvimGuiLinux.Avalonia.ViewModels;
using NvimGuiCommon.Explorer;

namespace NvimGuiLinux.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += MainWindow_Opened;
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            var grid = this.FindControl<LineGridControl>("EditorGrid");
            grid?.Bind(vm.Editor, vm.Grid);
            await vm.InitializeAsync();
        }
    }

    private void TreeView_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.SelectedNode = e.AddedItems.OfType<FileTreeNode>().FirstOrDefault();
        vm.PopulateSelectedDirectory();
    }

    private void TreeNode_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.OpenSelectedCommand.Execute(null);
    }

    private void OpenSelectedMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.OpenSelectedCommand.Execute(null);
    }

    private void CompareSelectedMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.CompareSelectedWithCurrentCommand.Execute(null);
    }
}
