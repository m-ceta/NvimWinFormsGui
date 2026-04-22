using System;
using System.IO;

namespace NvimGuiCommon.Config;

public static class ExeResolver
{
    public static string ResolveOnPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("command is empty");

        if (command.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
        {
            var full = Path.GetFullPath(command);
            if (File.Exists(full)) return full;
            throw new FileNotFoundException($"Executable not found: {full}", full);
        }

        var hasExt = Path.HasExtension(command);
        var exts = hasExt ? new[] { string.Empty } : (OperatingSystem.IsWindows() ? new[] { ".exe", ".com", ".bat", ".cmd" } : new[] { string.Empty, ".sh" });

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var d = dir.Trim().Trim('"');
            if (d.Length == 0) continue;
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(d, hasExt ? command : command + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }

        throw new FileNotFoundException($"'{command}' was not found on PATH.");
    }

    public static string Quote(string s)
        => (s.Contains(' ') || s.Contains('	')) ? $"\"{s}\"" : s;
}
