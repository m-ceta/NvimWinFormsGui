using NvimGuiCommon.Nvim;
using NvimGuiCommon.Diagnostics;
using System.Globalization;

namespace NvimGuiCommon.Editor;

public sealed class EditorController : IDisposable
{
    private readonly NvimSession _session = new();
    private int _cols = 100;
    private int _rows = 35;

    public EditorController()
    {
        Grid = new LineGridModel();
        _session.Redraw += events => Grid.ApplyRedraw(events);
        _session.Stderr += s => Stderr?.Invoke(s);
        _session.Faulted += ex => GuiLogger.Error(GuiLogCategory.RedrawEvent, () => $"NvimSession faulted error={ex}");
    }

    public LineGridModel Grid { get; }
    public event Action<string>? Stderr;

    public async Task StartAsync()
    {
        GuiLogger.Info(GuiLogCategory.Performance, () => $"EditorController.StartAsync begin cols={_cols} rows={_rows}");
        await _session.StartAsync();
        GuiLogger.Info(GuiLogCategory.Performance, () => "EditorController.StartAsync session started");
        await _session.AttachUiAsync(_cols, _rows);
        GuiLogger.Info(GuiLogCategory.Performance, () => "EditorController.StartAsync ui attached");
        await _session.CommandAsync("set guicursor=n-v-c:block,i-ci-ve:ver25,r-cr:hor20,o:hor50");
        await _session.CommandAsync("set laststatus=2");
        await _session.CommandAsync("set mouse=a");
        await _session.CommandAsync("redrawstatus!");
        GuiLogger.Info(GuiLogCategory.Performance, () => "EditorController.StartAsync commands completed");
    }

    public Task ResizeAsync(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        GuiLogger.Debug(GuiLogCategory.Resize, () => $"EditorController.ResizeAsync cols={cols} rows={rows}");
        return _session.ResizeAsync(cols, rows);
    }

    public Task InputAsync(string input, bool isTermcode)
    {
        GuiLogger.Debug(GuiLogCategory.Keyboard, () => $"EditorController.InputAsync data={Sanitize(input)} termcode={isTermcode}");
        if (!isTermcode)
            GuiLogger.Debug(GuiLogCategory.TextInput, () => $"EditorController.InputAsync data={Sanitize(input)} termcode={isTermcode}");
        return _session.InputAsync(input, isTermcode);
    }

    public Task MouseAsync(string button, string action, string modifiers, int grid, int row, int col)
    {
        GuiLogger.Debug(GuiLogCategory.Mouse, () => $"EditorController.MouseAsync button={button} action={action} modifiers={modifiers} grid={grid} row={row} col={col}");
        return _session.MouseAsync(button, action, modifiers, grid, row, col);
    }
    public Task SaveAsync() => _session.CommandAsync("write");
    public Task SaveAsAsync(string path) => _session.CommandAsync($"saveas {EscapeEx(path)}");
    public Task CloseBufferAsync() => _session.CommandAsync("bdelete");
    public Task SwitchTabAsync(int index) => index > 0 ? _session.CommandAsync($"tabnext {index}") : Task.CompletedTask;
    public Task CloseTabAsync(int index) => index > 0 ? _session.CommandAsync($"tabclose {index}") : Task.CompletedTask;
    public Task<string?> GetCurrentBufferPathAsync() => _session.GetCurrentBufferPathAsync();
    public Task DiffSplitAsync(string path) => _session.CommandAsync($"vert diffsplit {EscapeEx(path)}");
    public Task<MinimapSnapshot?> BuildMinimapSnapshotAsync() => BuildMinimapSnapshotCoreAsync();
    public Task ScrollToMinimapLineAsync(int line, int displayRow) => ScrollToMinimapLineCoreAsync(line, displayRow);

    public async Task OpenFileAsync(string fullPath, OpenMode mode)
    {
        var cmd = mode switch
        {
            OpenMode.NewTab => $"tabedit {EscapeEx(fullPath)}",
            OpenMode.Current => $"edit {EscapeEx(fullPath)}",
            OpenMode.VSplit => $"vsplit {EscapeEx(fullPath)}",
            OpenMode.Split => $"split {EscapeEx(fullPath)}",
            _ => $"edit {EscapeEx(fullPath)}",
        };
        await _session.CommandAsync(cmd);
        await _session.CommandAsync("redrawstatus!");
        await _session.CommandAsync("redraw!");
    }

    private static string EscapeEx(string path)
        => path.Replace(@"\", @"\\").Replace(" ", @"\ ").Replace("|", @"\|");

    private async Task<MinimapSnapshot?> BuildMinimapSnapshotCoreAsync()
    {
        var snapshotObj = await _session.CallAsync(
            "nvim_exec_lua",
            """
            local win = vim.api.nvim_get_current_win()
            local buf = vim.api.nvim_win_get_buf(win)
            local line_count = vim.api.nvim_buf_line_count(buf)
            local raw_lines = vim.api.nvim_buf_get_lines(buf, 0, -1, false)
            local wrap = vim.wo[win].wrap
            local info = vim.fn.getwininfo(win)[1] or {}
            local textoff = tonumber(info.textoff) or 0
            local text_width = math.max(1, vim.api.nvim_win_get_width(win) - textoff)

            local function line_rows(text)
              if not wrap then
                return 1
              end
              local width = vim.fn.strdisplaywidth(text or "")
              return math.max(1, math.ceil(math.max(width, 1) / text_width))
            end

            local normal_hex = vim.fn.synIDattr(vim.fn.hlID('Normal'), 'fg#', 'gui')
            if type(normal_hex) ~= 'string' or #normal_hex == 0 then
              normal_hex = '#c8d1dc'
            end

            local view = vim.fn.winsaveview()
            local topline = tonumber(view.topline) or 1
            local skipcol = tonumber(view.skipcol) or 0
            local current_line = vim.api.nvim_win_get_cursor(win)[1] or topline
            local current_col = vim.api.nvim_win_get_cursor(win)[2] or 0
            local win_height = math.max(1, vim.api.nvim_win_get_height(win))
            local bottom_line = math.min(line_count, topline + win_height - 1)

            local lines = {}
            local max_column = 1
            local total_display_rows = 0

            for i, text in ipairs(raw_lines) do
              local line = {
                rows = line_rows(text),
                width = vim.fn.strdisplaywidth(text or ""),
                color = normal_hex,
              }

              local first = text:find('%S')
              if first then
                local prefix = text:sub(1, first - 1)
                local last_text = text:match('^.*%S') or text
                local start_col = vim.fn.strdisplaywidth(prefix)
                local end_col = vim.fn.strdisplaywidth(last_text)
                local syn = vim.fn.synID(i, first, true)
                local color = vim.fn.synIDattr(vim.fn.synIDtrans(syn), 'fg#', 'gui')

                line.start = start_col
                line["end"] = math.max(start_col + 1, end_col)
                line.color = (type(color) == 'string' and #color > 0) and color or normal_hex
                max_column = math.max(max_column, line["end"], line.width)
              else
                max_column = math.max(max_column, line.width)
              end

              total_display_rows = total_display_rows + line.rows
              lines[i] = line
            end

            return {
              lineCount = math.max(line_count, #lines, 1),
              maxColumn = max_column,
              totalDisplayRows = math.max(total_display_rows, 1),
              textWidth = text_width,
              topLine = topline,
              bottomLine = bottom_line,
              currentLine = current_line,
              currentColumn = current_col,
              skipColumn = skipcol,
              lines = lines,
            }
            """,
            Array.Empty<object?>()).ConfigureAwait(false);

        var snapshot = NvimRpcClient.ToJsonable(snapshotObj) as IDictionary<string, object?>;
        if (snapshot is null)
            return null;

        var lineEntries = new List<MinimapLineInfo>();
        if (snapshot.TryGetValue("lines", out var linesObj))
        {
            if (linesObj is IList<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is IDictionary<string, object?> line)
                        lineEntries.Add(ParseMinimapLine(line));
                }
            }
            else if (linesObj is IDictionary<string, object?> map)
            {
                foreach (var key in map.Keys.Select(k => int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ? index : int.MaxValue).OrderBy(k => k))
                {
                    if (key == int.MaxValue)
                        continue;
                    if (map.TryGetValue(key.ToString(CultureInfo.InvariantCulture), out var lineObj) && lineObj is IDictionary<string, object?> line)
                        lineEntries.Add(ParseMinimapLine(line));
                }
            }
        }

        return new MinimapSnapshot(
            Math.Max(1, ToInt(snapshot, "lineCount")),
            Math.Max(1, ToInt(snapshot, "maxColumn")),
            Math.Max(1, ToInt(snapshot, "totalDisplayRows")),
            Math.Max(1, ToInt(snapshot, "textWidth")),
            Math.Max(1, ToInt(snapshot, "topLine")),
            Math.Max(1, ToInt(snapshot, "bottomLine")),
            Math.Max(1, ToInt(snapshot, "currentLine")),
            Math.Max(0, ToInt(snapshot, "currentColumn")),
            Math.Max(0, ToInt(snapshot, "skipColumn")),
            lineEntries);
    }

    private async Task ScrollToMinimapLineCoreAsync(int line, int displayRow)
    {
        await _session.CallAsync(
            "nvim_exec_lua",
            """
            local argv = { ... }
            local target_line = tonumber(argv[1])
            if not target_line then
              return
            end
            local win = vim.api.nvim_get_current_win()
            local buf = vim.api.nvim_win_get_buf(win)
            local maxline = vim.api.nvim_buf_line_count(buf)
            target_line = math.max(1, math.min(maxline, target_line))
            vim.api.nvim_win_set_cursor(win, { target_line, 0 })
            vim.cmd('normal! zz')
            """,
            new object?[] { line, displayRow }).ConfigureAwait(false);
    }

    private static MinimapLineInfo ParseMinimapLine(IDictionary<string, object?> line)
        => new(
            Math.Max(1, ToInt(line, "rows")),
            Math.Max(0, ToInt(line, "width")),
            line.TryGetValue("start", out var startObj) ? ToNullableInt(startObj) : null,
            line.TryGetValue("end", out var endObj) ? ToNullableInt(endObj) : null,
            line.TryGetValue("color", out var colorObj) ? Convert.ToString(colorObj, CultureInfo.InvariantCulture) : null);

    private static int ToInt(IDictionary<string, object?> values, string key)
        => values.TryGetValue(key, out var value) ? ToNullableInt(value) ?? 0 : 0;

    private static int? ToNullableInt(object? value)
    {
        if (value is null)
            return null;

        return value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            float f => (int)f,
            decimal m => (int)m,
            _ when int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string Sanitize(string value)
        => value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    public void Dispose() => _session.Dispose();
}
