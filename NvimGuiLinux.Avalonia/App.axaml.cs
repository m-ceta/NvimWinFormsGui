using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NvimGuiCommon.Diagnostics;
using NvimGuiLinux.Avalonia.Diagnostics;
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
            var logOptions = GuiLogOptions.FromEnvironmentAndArgs(desktop.Args);
            if (logOptions.Enabled)
                GuiLogger.Configure(logOptions, new AvaloniaGuiLogSink());
            else
                GuiLogger.Configure(logOptions);
            GuiLogger.Info(GuiLogCategory.Performance, () => $"GuiLogger enabled={logOptions.Enabled} level={logOptions.MinimumLevel} categories={logOptions.Categories} events={logOptions.LogEvents}");

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(desktop.Args ?? Array.Empty<string>())
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
