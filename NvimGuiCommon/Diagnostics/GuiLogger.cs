using System.Collections.Immutable;
using System.Diagnostics;

namespace NvimGuiCommon.Diagnostics;

public readonly record struct GuiLogEntry(
    DateTimeOffset Timestamp,
    GuiLogLevel Level,
    GuiLogCategory Category,
    int ThreadId,
    string Message);

public static class GuiLogger
{
    private static readonly object SyncRoot = new();
    private static GuiLogOptions _options = GuiLogOptions.Disabled;
    private static ImmutableArray<IGuiLogSink> _sinks = ImmutableArray<IGuiLogSink>.Empty;

    public static GuiLogOptions Options => _options;

    public static void Configure(GuiLogOptions options, params IGuiLogSink[] sinks)
    {
        lock (SyncRoot)
        {
            foreach (var sink in _sinks)
            {
                try { sink.Dispose(); } catch { }
            }

            _options = options;
            _sinks = options.Enabled
                ? sinks.Where(s => s is not null).ToImmutableArray()
                : ImmutableArray<IGuiLogSink>.Empty;
        }
    }

    public static bool IsEnabled(GuiLogCategory category, GuiLogLevel level = GuiLogLevel.Debug)
        => _options.IsEnabled(category, level);

    public static void Trace(GuiLogCategory category, Func<string> messageFactory) => Log(category, GuiLogLevel.Trace, messageFactory);
    public static void Debug(GuiLogCategory category, Func<string> messageFactory) => Log(category, GuiLogLevel.Debug, messageFactory);
    public static void Info(GuiLogCategory category, Func<string> messageFactory) => Log(category, GuiLogLevel.Info, messageFactory);
    public static void Warn(GuiLogCategory category, Func<string> messageFactory) => Log(category, GuiLogLevel.Warn, messageFactory);
    public static void Error(GuiLogCategory category, Func<string> messageFactory) => Log(category, GuiLogLevel.Error, messageFactory);

    public static void Log(GuiLogCategory category, GuiLogLevel level, string message)
    {
        if (!_options.IsEnabled(category, level))
            return;

        var entry = new GuiLogEntry(
            DateTimeOffset.Now,
            level,
            category,
            Environment.CurrentManagedThreadId,
            message);

        foreach (var sink in _sinks)
        {
            try { sink.Write(entry); } catch { }
        }
    }

    public static void Log(GuiLogCategory category, GuiLogLevel level, Func<string> messageFactory)
    {
        if (!_options.IsEnabled(category, level))
            return;
        Log(category, level, messageFactory());
    }
}
