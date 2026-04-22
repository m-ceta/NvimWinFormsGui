using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NvimGuiLinux.Avalonia.ViewModels;
using NvimGuiLinux.Avalonia.Views;

namespace NvimGuiLinux.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(desktop.Args ?? Array.Empty<string>())
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
