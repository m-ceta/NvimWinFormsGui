using System.Text;
using NvimGuiCommon.Diagnostics;

namespace NvimGuiLinux.Avalonia.Diagnostics;

public sealed class AvaloniaGuiLogSink : IGuiLogSink
{
    private readonly object _syncRoot = new();
    private readonly StreamWriter _writer;

    public AvaloniaGuiLogSink()
    {
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, $"nvim-gui-linux-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public void Write(in GuiLogEntry entry)
    {
        var line = $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{entry.Level}] [{entry.Category}] [tid:{entry.ThreadId}] {entry.Message}";
        lock (_syncRoot)
        {
            Console.WriteLine(line);
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _writer.Dispose();
        }
    }
}
