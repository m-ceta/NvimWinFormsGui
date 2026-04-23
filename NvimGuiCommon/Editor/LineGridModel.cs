using NvimGuiCommon.Nvim;

namespace NvimGuiCommon.Editor;

public sealed class LineGridModel
{
    private readonly Dictionary<int, GridState> _grids = new();
    private readonly Dictionary<int, CmdlineState> _cmdlines = new();
    private readonly List<string> _messages = new();
    private GridCell[][] _grid = Array.Empty<GridCell[]>();
    private readonly Dictionary<int, HighlightStyle> _highlights = new();
    private int _cursorGrid = 1;

    public int Rows { get; private set; }
    public int Cols { get; private set; }
    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    public int CursorGrid => _cursorGrid;
    public string DefaultBackground { get; private set; } = "#1e1e1e";
    public string DefaultForeground { get; private set; } = "#d4d4d4";
    public IReadOnlyDictionary<int, HighlightStyle> Highlights => _highlights;
    public GridCell[][] Grid => _grid;
    public IReadOnlyDictionary<int, GridState> Grids => _grids;
    public IReadOnlyList<string> Messages => _messages;
    public string ShowMode { get; private set; } = string.Empty;
    public string ShowCommand { get; private set; } = string.Empty;
    public CmdlineState? ActiveCmdline => _cmdlines.Count == 0
        ? null
        : _cmdlines.OrderBy(kv => kv.Key).Last().Value;
    public IEnumerable<GridState> VisibleGrids => _grids.Values
        .Where(g => g.Visible)
        .OrderBy(g => g.ZIndex)
        .ThenBy(g => g.Id);
    public event Action? Changed;

    public void ApplyRedraw(IReadOnlyList<object?> events)
    {
        foreach (var evt in events)
        {
            if (evt is not List<object?> update || update.Count == 0) continue;
            var name = Decode(update[0]);
            var args = update.Skip(1).ToArray();

            switch (name)
            {
                case "default_colors_set":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        var fg = ToHex(a.ElementAtOrDefault(0));
                        var bg = ToHex(a.ElementAtOrDefault(1));
                        if (!string.IsNullOrWhiteSpace(fg)) DefaultForeground = fg!;
                        if (!string.IsNullOrWhiteSpace(bg)) DefaultBackground = bg!;
                    }
                    break;
                case "hl_attr_define":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        var id = ToInt(a.ElementAtOrDefault(0));
                        var map = a.ElementAtOrDefault(1) as Dictionary<object, object?>;
                        string? fg = null; string? bg = null; string? sp = null;
                        var reverse = false; var bold = false; var italic = false;
                        var underline = false; var undercurl = false; var strikethrough = false;
                        if (map is not null)
                        {
                            foreach (var kv in map)
                            {
                                var k = Decode(kv.Key);
                                if (k == "foreground") fg = ToHex(kv.Value);
                                else if (k == "background") bg = ToHex(kv.Value);
                                else if (k == "special") sp = ToHex(kv.Value);
                                else if (k == "reverse") reverse = ToBool(kv.Value);
                                else if (k == "bold") bold = ToBool(kv.Value);
                                else if (k == "italic") italic = ToBool(kv.Value);
                                else if (k == "underline") underline = ToBool(kv.Value);
                                else if (k == "undercurl") undercurl = ToBool(kv.Value);
                                else if (k == "strikethrough") strikethrough = ToBool(kv.Value);
                            }
                        }
                        _highlights[id] = new HighlightStyle(fg, bg, sp, reverse, bold, italic, underline, undercurl, strikethrough);
                    }
                    break;
                case "grid_resize":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        Resize(
                            ToInt(a.ElementAtOrDefault(0)),
                            ToInt(a.ElementAtOrDefault(1)),
                            ToInt(a.ElementAtOrDefault(2)));
                    }
                    break;
                case "grid_clear":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        Clear(ToInt(a.ElementAtOrDefault(0)));
                    }
                    break;
                case "grid_cursor_goto":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        _cursorGrid = ToInt(a.ElementAtOrDefault(0));
                        CursorRow = ToInt(a.ElementAtOrDefault(1));
                        CursorCol = ToInt(a.ElementAtOrDefault(2));
                    }
                    break;
                case "grid_scroll":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        ApplyScroll(a);
                    }
                    break;
                case "grid_line":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        ApplyGridLine(a);
                    }
                    break;
                case "win_pos":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        var gridId = ToInt(a.ElementAtOrDefault(0));
                        var g = GetGrid(gridId);
                        g.Visible = true;
                        g.Floating = false;
                        g.Row = ToInt(a.ElementAtOrDefault(2));
                        g.Col = ToInt(a.ElementAtOrDefault(3));
                        g.ZIndex = gridId == 1 ? 0 : 100 + gridId;
                    }
                    break;
                case "win_float_pos":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        var gridId = ToInt(a.ElementAtOrDefault(0));
                        var anchorGridId = ToInt(a.ElementAtOrDefault(3));
                        _grids.TryGetValue(anchorGridId, out var anchorGrid);
                        var g = GetGrid(gridId);
                        g.Visible = true;
                        g.Floating = true;
                        g.Row = (anchorGrid?.Row ?? 0) + ToInt(a.ElementAtOrDefault(4));
                        g.Col = (anchorGrid?.Col ?? 0) + ToInt(a.ElementAtOrDefault(5));
                        var zindex = ToInt(a.ElementAtOrDefault(7));
                        g.ZIndex = zindex > 0 ? zindex : 1000 + gridId;
                    }
                    break;
                case "win_external_pos":
                    foreach (var item in args)
                    {
                        var gridId = ToInt(Normalize(item).ElementAtOrDefault(0));
                        if (_grids.TryGetValue(gridId, out var g)) g.Visible = false;
                    }
                    break;
                case "win_hide":
                case "win_close":
                    foreach (var item in args)
                    {
                        var gridId = ToInt(Normalize(item).ElementAtOrDefault(0));
                        if (_grids.TryGetValue(gridId, out var g)) g.Visible = false;
                    }
                    break;
                case "grid_destroy":
                    foreach (var item in args)
                    {
                        var gridId = ToInt(Normalize(item).ElementAtOrDefault(0));
                        if (gridId != 1) _grids.Remove(gridId);
                    }
                    break;
                case "cmdline_show":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        var level = Math.Max(1, ToInt(a.ElementAtOrDefault(5)));
                        _cmdlines[level] = new CmdlineState(
                            ChunksToText(a.ElementAtOrDefault(0)),
                            ToInt(a.ElementAtOrDefault(1)),
                            Decode(a.ElementAtOrDefault(2)),
                            Decode(a.ElementAtOrDefault(3)),
                            ToInt(a.ElementAtOrDefault(4)),
                            level);
                    }
                    break;
                case "cmdline_pos":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        var level = Math.Max(1, ToInt(a.ElementAtOrDefault(1)));
                        if (_cmdlines.TryGetValue(level, out var state))
                            _cmdlines[level] = state with { Position = ToInt(a.ElementAtOrDefault(0)) };
                    }
                    break;
                case "cmdline_special_char":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        var level = Math.Max(1, ToInt(a.ElementAtOrDefault(2)));
                        if (!_cmdlines.TryGetValue(level, out var state)) continue;
                        var text = state.Text.Insert(Math.Clamp(state.Position, 0, state.Text.Length), Decode(a.ElementAtOrDefault(0)));
                        _cmdlines[level] = state with { Text = text, Position = Math.Min(text.Length, state.Position + 1) };
                    }
                    break;
                case "cmdline_hide":
                    foreach (var item in args)
                    {
                        var level = Math.Max(1, ToInt(Normalize(item).ElementAtOrDefault(0)));
                        _cmdlines.Remove(level);
                    }
                    break;
                case "msg_show":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        var replaceLast = ToBool(a.ElementAtOrDefault(2));
                        var text = ChunksToText(a.ElementAtOrDefault(1)).TrimEnd('\r', '\n');
                        if (string.IsNullOrEmpty(text)) continue;
                        if (replaceLast && _messages.Count > 0) _messages[^1] = text;
                        else _messages.Add(text);
                        while (_messages.Count > 8) _messages.RemoveAt(0);
                    }
                    break;
                case "msg_showmode":
                    foreach (var item in args)
                        ShowMode = ChunksToText(Normalize(item).ElementAtOrDefault(0));
                    break;
                case "msg_showcmd":
                    foreach (var item in args)
                        ShowCommand = ChunksToText(Normalize(item).ElementAtOrDefault(0));
                    break;
                case "msg_clear":
                    _messages.Clear();
                    ShowMode = string.Empty;
                    ShowCommand = string.Empty;
                    break;
            }
        }
        Changed?.Invoke();
    }

    private void Resize(int gridId, int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;
        var g = GetGrid(gridId);
        var next = Enumerable.Range(0, rows)
            .Select(r => Enumerable.Range(0, cols)
                .Select(c => r < g.Rows && c < g.Cols ? g.Cells[r][c].Clone() : new GridCell())
                .ToArray())
            .ToArray();
        g.Cols = cols;
        g.Rows = rows;
        g.Cells = next;
        if (gridId == 1)
        {
            Cols = cols;
            Rows = rows;
            _grid = next;
        }
    }

    private void Clear(int gridId)
    {
        if (!_grids.TryGetValue(gridId, out var g)) return;
        foreach (var row in g.Cells)
            foreach (var cell in row)
            {
                cell.Ch = " ";
                cell.Hl = 0;
                cell.Continue = false;
            }
    }

    private void ApplyGridLine(object?[] a)
    {
        if (a.Length < 4) return;
        var gridId = ToInt(a[0]);
        if (!_grids.TryGetValue(gridId, out var g) || g.Rows == 0 || g.Cols == 0) return;
        var row = ToInt(a[1]);
        var col = ToInt(a[2]);
        if (row < 0 || row >= g.Rows) return;
        if (a[3] is not List<object?> cells) return;
        var lastHl = col > 0 && col - 1 < g.Cols ? g.Cells[row][col - 1].Hl : 0;

        foreach (var cellObj in cells)
        {
            if (cellObj is not List<object?> cell || cell.Count == 0) continue;
            var text = Convert.ToString(NvimRpcClient.ToJsonable(cell[0])) ?? string.Empty;
            var hl = cell.Count >= 2 && cell[1] is not null ? ToInt(cell[1]) : lastHl;
            var repeat = cell.Count >= 3 && cell[2] is not null ? ToInt(cell[2]) : 1;
            lastHl = hl;
            var chars = text.EnumerateRunes().Select(r => r.ToString()).ToArray();

            for (var rep = 0; rep < repeat; rep++)
            {
                if (chars.Length == 0)
                {
                    SetCell(g, row, col++, " ", hl);
                    continue;
                }
                foreach (var ch in chars) SetCell(g, row, col++, ch, hl);
            }
        }
    }

    private void ApplyScroll(object?[] a)
    {
        if (a.Length < 7) return;
        var gridId = ToInt(a[0]);
        if (!_grids.TryGetValue(gridId, out var g) || g.Rows == 0 || g.Cols == 0) return;
        var top = ToInt(a[1]); var bot = ToInt(a[2]);
        var left = ToInt(a[3]); var right = ToInt(a[4]);
        var delta = ToInt(a[5]); var colsDelta = ToInt(a[6]);
        if (colsDelta != 0 || delta == 0) return;
        top = Math.Clamp(top, 0, g.Rows);
        bot = Math.Clamp(bot, top, g.Rows);
        left = Math.Clamp(left, 0, g.Cols);
        right = Math.Clamp(right, left, g.Cols);

        var height = bot - top;
        var d = Math.Min(Math.Abs(delta), height);
        if (d <= 0) return;

        if (delta > 0)
        {
            for (var r = top; r < bot - d; r++)
                for (var c = left; c < right; c++) CopyCell(g, r + d, c, r, c);
            for (var r = bot - d; r < bot; r++)
                for (var c = left; c < right; c++) ResetCell(g, r, c);
        }
        else
        {
            for (var r = bot - 1; r >= top + d; r--)
                for (var c = left; c < right; c++) CopyCell(g, r - d, c, r, c);
            for (var r = top; r < top + d; r++)
                for (var c = left; c < right; c++) ResetCell(g, r, c);
        }
    }

    private void SetCell(GridState g, int row, int col, string ch, int hl)
    {
        if (!InRange(g, row, col)) return;
        g.Cells[row][col].Ch = string.IsNullOrEmpty(ch) ? " " : ch;
        g.Cells[row][col].Hl = hl;
        g.Cells[row][col].Continue = false;
    }

    private void CopyCell(GridState g, int sr, int sc, int dr, int dc)
    {
        if (!InRange(g, sr, sc) || !InRange(g, dr, dc)) return;
        g.Cells[dr][dc].Ch = g.Cells[sr][sc].Ch;
        g.Cells[dr][dc].Hl = g.Cells[sr][sc].Hl;
        g.Cells[dr][dc].Continue = g.Cells[sr][sc].Continue;
    }

    private void ResetCell(GridState g, int row, int col)
    {
        if (!InRange(g, row, col)) return;
        g.Cells[row][col].Ch = " ";
        g.Cells[row][col].Hl = 0;
        g.Cells[row][col].Continue = false;
    }

    private GridState GetGrid(int id)
    {
        if (!_grids.TryGetValue(id, out var g))
        {
            g = new GridState(id) { Visible = id == 1, ZIndex = id == 1 ? 0 : 100 + id };
            _grids[id] = g;
        }
        return g;
    }

    private static bool InRange(GridState g, int row, int col) => row >= 0 && row < g.Rows && col >= 0 && col < g.Cols;
    private static object?[] Normalize(object? item) => item is List<object?> list ? list.ToArray() : [item];
    private static string Decode(object? v) => NvimRpcClient.ToJsonable(v)?.ToString() ?? string.Empty;
    private static string ChunksToText(object? chunks)
    {
        if (chunks is not List<object?> list) return Decode(chunks);

        var parts = new List<string>();
        foreach (var item in list)
        {
            if (item is List<object?> chunk)
            {
                var text = chunk.Count >= 2 ? Decode(chunk[1]) : Decode(chunk.FirstOrDefault());
                parts.Add(text);
            }
            else
            {
                parts.Add(Decode(item));
            }
        }
        return string.Concat(parts);
    }
    private static bool ToBool(object? v) => NvimRpcClient.ToJsonable(v) switch { bool b => b, long l => l != 0, int i => i != 0, _ => false };
    private static int ToInt(object? v) => NvimRpcClient.ToJsonable(v) switch { int i => i, long l => (int)l, double d => (int)Math.Round(d), float f => (int)Math.Round(f), string s when int.TryParse(s, out var n) => n, _ => 0 };
    private static string? ToHex(object? v) => NvimRpcClient.ToJsonable(v) switch { int i => $"#{i & 0xFFFFFF:X6}".ToLowerInvariant(), long l => $"#{l & 0xFFFFFF:X6}".ToLowerInvariant(), _ => null };
}

public sealed record CmdlineState(string Text, int Position, string FirstChar, string Prompt, int Indent, int Level);

public sealed class GridState(int id)
{
    public int Id { get; } = id;
    public int Rows { get; set; }
    public int Cols { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    public int ZIndex { get; set; }
    public bool Visible { get; set; }
    public bool Floating { get; set; }
    public GridCell[][] Cells { get; set; } = Array.Empty<GridCell[]>();
}
