using System.Globalization;

namespace NvimGuiCommon.Diagnostics;

public sealed record GuiLogOptions(
    bool Enabled,
    GuiLogLevel MinimumLevel,
    GuiLogCategory Categories,
    bool LogEvents)
{
    public static GuiLogOptions Disabled { get; } = new(false, GuiLogLevel.Info, GuiLogCategory.None, false);

    public bool IsEnabled(GuiLogCategory category, GuiLogLevel level)
        => Enabled && level >= MinimumLevel && (Categories == GuiLogCategory.All || (Categories & category) != 0);

    public static GuiLogOptions FromEnvironmentAndArgs(IEnumerable<string>? args)
    {
        var argList = args?.ToArray() ?? Array.Empty<string>();
        var enabled = ReadBool("NVIM_GUI_LOG") || argList.Any(a => string.Equals(a, "--gui-log", StringComparison.OrdinalIgnoreCase));
        var level = ParseLevel(ReadValue("NVIM_GUI_LOG_LEVEL") ?? ReadArgValue(argList, "--gui-log-level")) ?? GuiLogLevel.Info;
        var categories = ParseCategories(ReadValue("NVIM_GUI_LOG_CATEGORIES") ?? ReadArgValue(argList, "--gui-log-categories"));
        var logEvents = ReadBool("NVIM_GUI_LOG_EVENTS") || argList.Any(a => string.Equals(a, "--gui-log-events", StringComparison.OrdinalIgnoreCase));

        if (!enabled)
            return Disabled;

        return new GuiLogOptions(true, level, categories, logEvents);
    }

    private static string? ReadValue(string name)
        => Environment.GetEnvironmentVariable(name);

    private static bool ReadBool(string name)
    {
        var value = ReadValue(name);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        return value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadArgValue(IEnumerable<string> args, string prefix)
    {
        var match = args.FirstOrDefault(a => a.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase));
        return match is null ? null : match[(prefix.Length + 1)..];
    }

    private static GuiLogLevel? ParseLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Enum.TryParse<GuiLogLevel>(value.Trim(), true, out var level)
            ? level
            : null;
    }

    private static GuiLogCategory ParseCategories(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return GuiLogCategory.All;

        var result = GuiLogCategory.None;
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals("All", StringComparison.OrdinalIgnoreCase))
                return GuiLogCategory.All;

            if (Enum.TryParse<GuiLogCategory>(token, true, out var category))
                result |= category;
        }

        return result == GuiLogCategory.None ? GuiLogCategory.All : result;
    }
}
