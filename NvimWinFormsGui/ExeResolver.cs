using System;
using System.IO;

namespace NvimWinFormsGui;

public static class ExeResolver
{
    // PATH から実行ファイルを解決してフルパスを返す（ユーザーはフルパス指定不要）
    public static string ResolveOnPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("command is empty");

        // すでにパスっぽい（\ / を含む）ならそのまま確認
        if (command.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
        {
            var full = Path.GetFullPath(command);
            if (File.Exists(full)) return full;
            throw new FileNotFoundException($"Executable not found: {full}", full);
        }

        // ここでは exe/com を優先（cmd/bat を優先してしまうと面倒が増える）
        var hasExt = Path.HasExtension(command);
        var exts = hasExt ? new[] { "" } : new[] { ".exe", ".com", ".bat", ".cmd" };

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var d = dir.Trim().Trim('"');
            if (d.Length == 0) continue;

            foreach (var ext in exts)
            {
                var cand = Path.Combine(d, hasExt ? command : command + ext);
                if (File.Exists(cand)) return cand;
            }
        }

        throw new FileNotFoundException($"'{command}' が PATH 上で見つかりません。コマンドプロンプトで `where {command}` を確認してください。");
    }

    public static string Quote(string s)
        => (s.Contains(' ') || s.Contains('\t')) ? $"\"{s}\"" : s;
}