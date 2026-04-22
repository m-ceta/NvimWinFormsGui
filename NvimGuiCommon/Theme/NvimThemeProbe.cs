using NvimGuiCommon.Config;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NvimWinFormsGui
{
    public static class NvimThemeProbe
    {
        public static async Task<(string? bg, string? fg)?> TryGetThemeAsync(string nvimCommand)
        {
            var resolved = ExeResolver.ResolveOnPath(nvimCommand);

            // 重要:
            // - termguicolors を ON
            // - もし colors_name があるなら colorscheme を再適用（headless のタイミング差を吸収）
            // - Normal の bg/fg を nvim_get_hl で取得
            var lua = @"
vim.o.termguicolors = true
local cs = vim.g.colors_name
if cs and cs ~= '' then pcall(vim.cmd.colorscheme, cs) end

local function tohex(n)
  if type(n) ~= 'number' then return '' end
  return string.format('#%06x', n)
end

local ok, hl = pcall(vim.api.nvim_get_hl, 0, { name = 'Normal', link = false })
if not ok then
  -- 古い環境向けフォールバック（返却キーが違うことがある）
  local h2 = vim.api.nvim_get_hl_by_name('Normal', true)
  io.write('@@THEME@@' .. tohex(h2.background) .. '|' .. tohex(h2.foreground) .. '@@END@@')
  return
end

io.write('@@THEME@@' .. tohex(hl.bg) .. '|' .. tohex(hl.fg) .. '@@END@@')
".Replace("\r", "").Replace("\n", " ");

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

            var m = Regex.Match(stdout, "@@THEME@@(.*?)@@END@@", RegexOptions.Singleline);
            if (!m.Success) return null;

            var parts = m.Groups[1].Value.Split('|');
            var bg = parts.Length > 0 ? parts[0].Trim() : "";
            var fg = parts.Length > 1 ? parts[1].Trim() : "";

            return (string.IsNullOrEmpty(bg) ? null : bg, string.IsNullOrEmpty(fg) ? null : fg);
        }
    }
}
