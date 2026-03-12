using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NvimWinFormsGui;

public static class NvimFontProbe
{
    // nvimCommand は "nvim" でOK（PATH解決は ExeResolver.ResolveOnPath を使う想定）
    public static async Task<(string familyCss, int size)?> TryGetFontAsync(string nvimCommand)
    {
        var resolved = ExeResolver.ResolveOnPath(nvimCommand);
        if (!File.Exists(resolved)) return null;

        // guifont をマーカーで囲って取得
        string lua = "io.write('@@GUIFONT@@'..(vim.o.guifont or '')..'@@END@@')";

        var psi = new ProcessStartInfo
        {
            FileName = resolved,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add($"+lua {lua}");
        psi.ArgumentList.Add("+q");

        using var p = Process.Start(psi);
        if (p is null) return null;

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask, p.WaitForExitAsync());

        var stdout = stdoutTask.Result;

        var m = Regex.Match(stdout, "@@GUIFONT@@(.*?)@@END@@", RegexOptions.Singleline);
        if (!m.Success) return null;

        var guifont = m.Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(guifont)) return null;

        var (families, size) = ParseGuiFontToCssFamilies(guifont);
        if (families.Length == 0) return null;

        // 末尾に monospace を保険で付ける（任意）
        var css = string.Join(", ", families) + ", monospace";
        return (css, size);
    }

    private static (string[] familiesCss, int size) ParseGuiFontToCssFamilies(string guifont)
    {
        // guifont: "Font\ Name:h14,Other\ Font:h14" のように複数指定もあり
        var tokens = SplitByUnescapedComma(guifont);

        int size = 14;
        var families = new System.Collections.Generic.List<string>();

        foreach (var tok in tokens)
        {
            if (string.IsNullOrWhiteSpace(tok)) continue;

            // :hNN を拾う（最初に見つかったものを採用）
            var hm = Regex.Match(tok, @":h(\d+)", RegexOptions.IgnoreCase);
            if (hm.Success && int.TryParse(hm.Groups[1].Value, out var h))
                size = h;

            // フォント名部分（最初の ":" より前）
            var namePart = tok;
            var colon = IndexOfUnescaped(tok, ':');
            if (colon >= 0) namePart = tok.Substring(0, colon);

            var name = UnescapeVim(namePart).Trim();
            if (name.Length == 0) continue;

            families.Add(ToCssFontFamily(name));
        }

        return (families.ToArray(), size);
    }

    private static string[] SplitByUnescapedComma(string s)
    {
        var list = new System.Collections.Generic.List<string>();
        var sb = new StringBuilder();
        bool esc = false;

        foreach (var ch in s)
        {
            if (esc)
            {
                sb.Append(ch);
                esc = false;
                continue;
            }
            if (ch == '\\')
            {
                sb.Append(ch);
                esc = true;
                continue;
            }
            if (ch == ',')
            {
                list.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        list.Add(sb.ToString());
        return list.ToArray();
    }

    private static int IndexOfUnescaped(string s, char target)
    {
        bool esc = false;
        for (int i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (esc) { esc = false; continue; }
            if (ch == '\\') { esc = true; continue; }
            if (ch == target) return i;
        }
        return -1;
    }

    private static string UnescapeVim(string s)
    {
        // \  \, \: \\ を最低限戻す
        return s
            .Replace(@"\\", @"\")
            .Replace(@"\ ", " ")
            .Replace(@"\,", ",")
            .Replace(@"\:", ":");
    }

    private static string ToCssFontFamily(string name)
    {
        // 空白や記号がある場合はクォート
        if (name.IndexOfAny(new[] { ' ', '\t', '-', '(', ')', '[', ']', '{', '}', ',' }) >= 0)
            return $"\"{name.Replace("\"", "\\\"")}\"";
        return name;
    }
}