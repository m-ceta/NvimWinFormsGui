using NvimGuiCommon.Nvim;

namespace NvimGuiCommon.Editor;

public sealed class LineGridModel
{
    private GridCell[][] _grid = Array.Empty<GridCell[]>();
    private readonly Dictionary<int, HighlightStyle> _highlights = new();

    public int Rows { get; private set; }
    public int Cols { get; private set; }
    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    public string DefaultBackground { get; private set; } = "#1e1e1e";
    public string DefaultForeground { get; private set; } = "#d4d4d4";
    public IReadOnlyDictionary<int, HighlightStyle> Highlights => _highlights;
    public GridCell[][] Grid => _grid;
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
                        string? fg = null; string? bg = null; var reverse = false;
                        if (map is not null)
                        {
                            foreach (var kv in map)
                            {
                                var k = Decode(kv.Key);
                                if (k == "foreground") fg = ToHex(kv.Value);
                                else if (k == "background") bg = ToHex(kv.Value);
                                else if (k == "reverse") reverse = ToBool(kv.Value);
                            }
                        }
                        _highlights[id] = new HighlightStyle(fg, bg, reverse);
                    }
                    break;
                case "grid_resize":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        if (ToInt(a.ElementAtOrDefault(0)) != 1) continue;
                        Resize(ToInt(a.ElementAtOrDefault(1)), ToInt(a.ElementAtOrDefault(2)));
                    }
                    break;
                case "grid_clear":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        if (ToInt(a.ElementAtOrDefault(0)) == 1) Clear();
                    }
                    break;
                case "grid_cursor_goto":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        if (ToInt(a.ElementAtOrDefault(0)) != 1) continue;
                        CursorRow = ToInt(a.ElementAtOrDefault(1));
                        CursorCol = ToInt(a.ElementAtOrDefault(2));
                    }
                    break;
                case "grid_scroll":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        if (ToInt(a.ElementAtOrDefault(0)) != 1) continue;
                        ApplyScroll(a);
                    }
                    break;
                case "grid_line":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        if (ToInt(a.ElementAtOrDefault(0)) != 1) continue;
                        ApplyGridLine(a);
                    }
                    break;
            }
        }
        Changed?.Invoke();
    }

    private void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;
        Cols = cols;
        Rows = rows;
        _grid = Enumerable.Range(0, rows)
            .Select(_ => Enumerable.Range(0, cols).Select(__ => new GridCell()).ToArray())
            .ToArray();
    }

    private void Clear()
    {
        foreach (var row in _grid)
            foreach (var cell in row)
            {
                cell.Ch = " ";
                cell.Hl = 0;
                cell.Continue = false;
            }
    }

    private void ApplyGridLine(object?[] a)
    {
        if (a.Length < 4 || Rows == 0 || Cols == 0) return;
        var row = ToInt(a[1]);
        var col = ToInt(a[2]);
        if (row < 0 || row >= Rows) return;
        if (a[3] is not List<object?> cells) return;
        var lastHl = col > 0 && col - 1 < Cols ? _grid[row][col - 1].Hl : 0;

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
                    SetCell(row, col++, " ", hl);
                    continue;
                }
                foreach (var ch in chars) SetCell(row, col++, ch, hl);
            }
        }
    }

    private void ApplyScroll(object?[] a)
    {
        if (a.Length < 7 || Rows == 0 || Cols == 0) return;
        var top = ToInt(a[1]); var bot = ToInt(a[2]);
        var left = ToInt(a[3]); var right = ToInt(a[4]);
        var delta = ToInt(a[5]); var colsDelta = ToInt(a[6]);
        if (colsDelta != 0 || delta == 0) return;

        if (delta > 0)
        {
            for (var r = top; r < bot - delta; r++)
                for (var c = left; c < right; c++) CopyCell(r + delta, c, r, c);
            for (var r = bot - delta; r < bot; r++)
                for (var c = left; c < right; c++) ResetCell(r, c);
        }
        else
        {
            var d = -delta;
            for (var r = bot - 1; r >= top + d; r--)
                for (var c = left; c < right; c++) CopyCell(r - d, c, r, c);
            for (var r = top; r < top + d; r++)
                for (var c = left; c < right; c++) ResetCell(r, c);
        }
    }

    private void SetCell(int row, int col, string ch, int hl)
    {
        if (!InRange(row, col)) return;
        _grid[row][col].Ch = string.IsNullOrEmpty(ch) ? " " : ch;
        _grid[row][col].Hl = hl;
        _grid[row][col].Continue = false;
    }

    private void CopyCell(int sr, int sc, int dr, int dc)
    {
        if (!InRange(sr, sc) || !InRange(dr, dc)) return;
        _grid[dr][dc].Ch = _grid[sr][sc].Ch;
        _grid[dr][dc].Hl = _grid[sr][sc].Hl;
        _grid[dr][dc].Continue = _grid[sr][sc].Continue;
    }

    private void ResetCell(int row, int col)
    {
        if (!InRange(row, col)) return;
        _grid[row][col].Ch = " ";
        _grid[row][col].Hl = 0;
        _grid[row][col].Continue = false;
    }

    private bool InRange(int row, int col) => row >= 0 && row < Rows && col >= 0 && col < Cols;
    private static object?[] Normalize(object? item) => item is List<object?> list ? list.ToArray() : [item];
    private static string Decode(object? v) => NvimRpcClient.ToJsonable(v)?.ToString() ?? string.Empty;
    private static bool ToBool(object? v) => NvimRpcClient.ToJsonable(v) switch { bool b => b, long l => l != 0, int i => i != 0, _ => false };
    private static int ToInt(object? v) => NvimRpcClient.ToJsonable(v) switch { int i => i, long l => (int)l, string s when int.TryParse(s, out var n) => n, _ => 0 };
    private static string? ToHex(object? v) => NvimRpcClient.ToJsonable(v) switch { int i => $"#{i & 0xFFFFFF:X6}".ToLowerInvariant(), long l => $"#{l & 0xFFFFFF:X6}".ToLowerInvariant(), _ => null };
}
