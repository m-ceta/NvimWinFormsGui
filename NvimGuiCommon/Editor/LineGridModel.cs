using System.Globalization;
using System.Text;
using NvimGuiCommon.Diagnostics;
using NvimGuiCommon.Nvim;

namespace NvimGuiCommon.Editor;

public sealed class LineGridModel
{
    private static readonly bool StatuslineDebugEnabled =
        string.Equals(Environment.GetEnvironmentVariable("NVIM_AVALONIA_STATUS_DEBUG"), "1", StringComparison.Ordinal);
    private static readonly string StatuslineDebugPath = Path.Combine(
        AppContext.BaseDirectory,
        "statusline-debug.log");

    private readonly Dictionary<int, GridState> _grids = new();
    private readonly Dictionary<int, CmdlineState> _cmdlines = new();
    private readonly List<string> _messages = new();
    private readonly List<MessageEntry> _messageEntries = new();
    private readonly List<MessageEntry> _historyEntries = new();
    private readonly List<string> _cmdlineBlock = new();
    private readonly Dictionary<int, HighlightStyle> _highlights = new();
    private GridCell[][] _grid = Array.Empty<GridCell[]>();
    private int _cursorGrid = 1;
    private string _currentModeName = "normal";
    private bool _historyVisible;
    private int _transientMessageGeneration;

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
    public IReadOnlyList<MessageEntry> MessageEntries => _messageEntries;
    public IReadOnlyList<MessageEntry> HistoryEntries => _historyEntries;
    public string ShowMode { get; private set; } = string.Empty;
    public string ShowCommand { get; private set; } = string.Empty;
    public string Ruler { get; private set; } = string.Empty;
    public int? MessageGridId { get; private set; }
    public int? MessageGridRow { get; private set; }
    public IReadOnlyList<string> CmdlineBlock => _cmdlineBlock;
    public IReadOnlyDictionary<int, CmdlineState> Cmdlines => _cmdlines;
    public CmdlineState? ActiveCmdline => _cmdlines.Count == 0
        ? null
        : _cmdlines.OrderBy(kv => kv.Key).Last().Value;
    public PopupMenuState? PopupMenuState { get; private set; }
    public TablineState TablineState { get; private set; } = TablineState.Empty;
    public IReadOnlyList<CursorModeState> CursorModes { get; private set; } = Array.Empty<CursorModeState>();
    public string CurrentModeName => _currentModeName;
    public CursorModeState CurrentCursorModeState { get; private set; } = CursorModeState.Default;
    public string CursorShape => CurrentCursorModeState.CursorShape;
    public bool CursorVisible { get; private set; } = true;
    public bool HistoryVisible => _historyVisible;
    public int TransientMessageGeneration => _transientMessageGeneration;
    public int EditorTopOffset => TablineState.Tabs.Count > 0 ? 1 : 0;
    public IEnumerable<GridState> VisibleGrids => _grids.Values
        .Where(g => g.Visible && (!MessageGridId.HasValue || g.Id != MessageGridId.Value))
        .OrderBy(g => g.EffectiveZIndex)
        .ThenBy(g => g.Id);
    public event Action? Changed;

    public void ApplyRedraw(IReadOnlyList<object?> events)
    {
        foreach (var evt in events)
        {
            if (evt is not List<object?> update || update.Count == 0) continue;
            var name = Decode(update[0]);
            var args = update.Skip(1).ToArray();
            GuiLogger.Debug(GuiLogCategory.RedrawEvent, () => $"redraw name={name} argc={args.Length}");
            if (GuiLogger.Options.LogEvents)
                GuiLogger.Trace(GuiLogCategory.RedrawEvent, () => $"redraw name={name} argc={args.Length}");

            try
            {
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
                        var map = AsMap(a.ElementAtOrDefault(1));
                        string? fg = null;
                        string? bg = null;
                        string? sp = null;
                        var reverse = false;
                        var bold = false;
                        var italic = false;
                        var underline = false;
                        var undercurl = false;
                        var strikethrough = false;
                        var blend = 0;

                        if (map is not null)
                        {
                            foreach (var kv in map)
                            {
                                var key = Decode(kv.Key);
                                if (key == "foreground") fg = ToHex(kv.Value);
                                else if (key == "background") bg = ToHex(kv.Value);
                                else if (key == "special") sp = ToHex(kv.Value);
                                else if (key == "reverse") reverse = ToBool(kv.Value);
                                else if (key == "bold") bold = ToBool(kv.Value);
                                else if (key == "italic") italic = ToBool(kv.Value);
                                else if (key == "underline") underline = ToBool(kv.Value);
                                else if (key == "undercurl") undercurl = ToBool(kv.Value);
                                else if (key == "strikethrough") strikethrough = ToBool(kv.Value);
                                else if (key == "blend") blend = ToInt(kv.Value);
                            }
                        }

                        _highlights[id] = new HighlightStyle(fg, bg, sp, reverse, bold, italic, underline, undercurl, strikethrough, blend);
                    }
                    break;

                case "grid_resize":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        Resize(ToInt(a.ElementAtOrDefault(0)), ToInt(a.ElementAtOrDefault(1)), ToInt(a.ElementAtOrDefault(2)));
                    }
                    break;

                case "grid_clear":
                    foreach (var item in args)
                        Clear(ToInt(Normalize(item).ElementAtOrDefault(0)));
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
                        ApplyScroll(Normalize(item));
                    break;

                case "grid_line":
                    foreach (var item in args)
                        ApplyGridLine(Normalize(item));
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
                        g.RenderRow = g.Row;
                        g.RenderCol = g.Col;
                        g.AnchorGrid = 1;
                        g.AnchorRow = 0;
                        g.AnchorCol = 0;
                        g.FloatAnchor = "NW";
                        g.Focusable = true;
                        g.ZIndex = gridId == 1 ? 0 : 100 + gridId;
                        GuiLogger.Debug(GuiLogCategory.FloatingGrid, () => $"win_pos grid={gridId} row={g.Row} col={g.Col} zindex={g.ZIndex} effective_zindex={g.EffectiveZIndex}");
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
                        g.FloatAnchor = Decode(a.ElementAtOrDefault(2));
                        g.AnchorGrid = anchorGridId;
                        g.AnchorRow = ToInt(a.ElementAtOrDefault(4));
                        g.AnchorCol = ToInt(a.ElementAtOrDefault(5));
                        g.Focusable = ToBool(a.ElementAtOrDefault(6));
                        g.Row = (anchorGrid?.Row ?? 0) + g.AnchorRow;
                        g.Col = (anchorGrid?.Col ?? 0) + g.AnchorCol;
                        g.RenderRow = g.Row;
                        g.RenderCol = g.Col;
                        var zindex = ToInt(a.ElementAtOrDefault(7));
                        g.ZIndex = zindex > 0 ? zindex : 1000 + gridId;
                        GuiLogger.Debug(GuiLogCategory.FloatingGrid, () => $"win_float_pos grid={gridId} anchor={g.FloatAnchor} anchor_grid={anchorGridId} anchor_row={g.AnchorRow} anchor_col={g.AnchorCol} row={g.Row} col={g.Col} focusable={g.Focusable} zindex={g.ZIndex} effective_zindex={g.EffectiveZIndex}");
                    }
                    break;

                case "win_external_pos":
                case "win_hide":
                case "win_close":
                    foreach (var item in args)
                    {
                        var gridId = ToInt(Normalize(item).ElementAtOrDefault(0));
                        if (_grids.TryGetValue(gridId, out var g))
                        {
                            g.Visible = false;
                            GuiLogger.Debug(GuiLogCategory.FloatingGrid, () => $"{name} grid={gridId} visible=false");
                        }
                    }
                    break;

                case "grid_destroy":
                    foreach (var item in args)
                    {
                        var gridId = ToInt(Normalize(item).ElementAtOrDefault(0));
                        if (gridId != 1)
                        {
                            _grids.Remove(gridId);
                            GuiLogger.Debug(GuiLogCategory.FloatingGrid, () => $"grid_destroy grid={gridId}");
                        }
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
                            level,
                            string.Empty,
                            true,
                            NormalizeMessageChunks(a.ElementAtOrDefault(0)));
                        GuiLogger.Debug(GuiLogCategory.Cmdline, () => $"cmdline_show level={level} text={Sanitize(_cmdlines[level].Text)} pos={_cmdlines[level].Position} first={_cmdlines[level].FirstChar} prompt={_cmdlines[level].Prompt} indent={_cmdlines[level].Indent}");
                    }
                    break;

                case "cmdline_pos":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        var level = Math.Max(1, ToInt(a.ElementAtOrDefault(1)));
                        if (_cmdlines.TryGetValue(level, out var state))
                            _cmdlines[level] = state with { Position = ToInt(a.ElementAtOrDefault(0)) };
                        GuiLogger.Debug(GuiLogCategory.Cmdline, () => $"cmdline_pos level={level} pos={ToInt(a.ElementAtOrDefault(0))}");
                    }
                    break;

                case "cmdline_special_char":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        var level = Math.Max(1, ToInt(a.ElementAtOrDefault(2)));
                        if (!_cmdlines.TryGetValue(level, out var state)) continue;
                        _cmdlines[level] = state with
                        {
                            SpecialChar = Decode(a.ElementAtOrDefault(0)),
                            SpecialShift = ToBool(a.ElementAtOrDefault(1))
                        };
                        GuiLogger.Debug(GuiLogCategory.Cmdline, () => $"cmdline_special_char level={level} char={Sanitize(Decode(a.ElementAtOrDefault(0)))} shift={ToBool(a.ElementAtOrDefault(1))}");
                    }
                    break;

                case "cmdline_hide":
                    foreach (var item in args)
                    {
                        var level = Math.Max(1, ToInt(Normalize(item).ElementAtOrDefault(0)));
                        _cmdlines.Remove(level);
                        GuiLogger.Debug(GuiLogCategory.Cmdline, () => $"cmdline_hide level={level}");
                    }
                    break;

                case "popupmenu_show":
                    foreach (var item in args)
                    {
                        PopupMenuState = ParsePopupMenuState(Normalize(item), PopupMenuState);
                        if (PopupMenuState is not null)
                        {
                            GuiLogger.Debug(GuiLogCategory.PopupMenu, () => $"popupmenu_show items={PopupMenuState.Items.Count} selected={PopupMenuState.Selected} grid={PopupMenuState.Grid} row={PopupMenuState.Row} col={PopupMenuState.Col} anchor={PopupMenuState.AnchorKind} cmdline={PopupMenuState.IsCmdline}");
                        }
                    }
                    break;

                case "popupmenu_select":
                    foreach (var item in args)
                    {
                        if (PopupMenuState is null) continue;
                        var selected = ToInt(Normalize(item).ElementAtOrDefault(0));
                        PopupMenuState = PopupMenuState with { Selected = selected };
                        GuiLogger.Debug(GuiLogCategory.PopupMenu, () => $"popupmenu_select selected={selected}");
                    }
                    break;

                case "popupmenu_hide":
                    PopupMenuState = null;
                    GuiLogger.Debug(GuiLogCategory.PopupMenu, () => "popupmenu_hide");
                    break;

                case "tabline_update":
                    foreach (var item in args)
                        TablineState = ParseTablineState(Normalize(item));
                    break;

                case "mode_info_set":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        if (!ToBool(a.ElementAtOrDefault(0))) continue;
                        CursorModes = ParseCursorModes(a.ElementAtOrDefault(1));
                        CurrentCursorModeState = ResolveCursorMode(_currentModeName);
                    }
                    break;

                case "mode_change":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        _currentModeName = Decode(a.ElementAtOrDefault(0));
                        CurrentCursorModeState = ResolveCursorMode(_currentModeName);
                        if (!_currentModeName.StartsWith("cmdline", StringComparison.OrdinalIgnoreCase))
                            ShowCommand = string.Empty;
                    }
                    break;

                case "msg_show":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        var kind = Decode(a.ElementAtOrDefault(0));
                        var replaceLast = ToBool(a.ElementAtOrDefault(2));
                        var text = ChunksToText(a.ElementAtOrDefault(1)).TrimEnd('\r', '\n');
                        var chunks = NormalizeMessageChunks(a.ElementAtOrDefault(1));
                        if (string.IsNullOrEmpty(text)) continue;
                        var entry = new MessageEntry(text, kind, chunks, false);
                        if (replaceLast && _messages.Count > 0) _messages[^1] = text;
                        else _messages.Add(text);
                        if (replaceLast && _messageEntries.Count > 0) _messageEntries[^1] = entry;
                        else _messageEntries.Add(entry);
                        while (_messages.Count > 12) _messages.RemoveAt(0);
                        while (_messageEntries.Count > 12) _messageEntries.RemoveAt(0);
                        _transientMessageGeneration++;
                        GuiLogger.Debug(GuiLogCategory.Message, () => $"msg_show kind={kind} replace={replaceLast} text={Sanitize(text)}");
                    }
                    break;

                case "msg_set_pos":
                    foreach (var item in args)
                    {
                        var a = Normalize(item);
                        MessageGridId = ToInt(a.ElementAtOrDefault(0));
                        MessageGridRow = ToInt(a.ElementAtOrDefault(1));
                        GuiLogger.Debug(GuiLogCategory.Message, () => $"msg_set_pos grid={MessageGridId} row={MessageGridRow}");
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

                case "msg_ruler":
                    foreach (var item in args)
                        Ruler = ChunksToText(Normalize(item).ElementAtOrDefault(0));
                    break;

                case "msg_history_show":
                    _historyVisible = true;
                    _historyEntries.Clear();
                    foreach (var item in args)
                    {
                        var lines = Normalize(item).ElementAtOrDefault(0) as List<object?>;
                        if (lines is null) continue;
                        foreach (var line in lines)
                        {
                            var entry = Normalize(line);
                            var kind = Decode(entry.ElementAtOrDefault(0));
                            var chunks = NormalizeMessageChunks(entry.ElementAtOrDefault(1));
                            var text = string.Concat(chunks.Select(c => c.Text));
                            if (string.IsNullOrEmpty(text)) continue;
                                var append = ToBool(entry.ElementAtOrDefault(2));
                                var prefixedChunks = chunks;
                                var prefixedText = text;
                                if (!string.IsNullOrWhiteSpace(kind))
                                {
                                    prefixedChunks = [new MessageChunk($"[{kind}] ", 0), .. chunks];
                                    prefixedText = $"[{kind}] {text}";
                                }
                                if (append && _historyEntries.Count > 0)
                                {
                                    var prev = _historyEntries[^1];
                                    _historyEntries[^1] = prev with
                                    {
                                        Text = prev.Text + prefixedText,
                                        Chunks = prev.Chunks.Concat(prefixedChunks).ToArray(),
                                        History = true
                                    };
                                }
                                else
                                {
                                    _historyEntries.Add(new MessageEntry(prefixedText, kind, prefixedChunks, true));
                                }
                            }
                        }
                    break;

                case "msg_history_clear":
                    _historyVisible = false;
                    _historyEntries.Clear();
                    _messageEntries.Clear();
                    _messages.Clear();
                    ShowMode = string.Empty;
                    ShowCommand = string.Empty;
                    Ruler = string.Empty;
                    MessageGridId = null;
                    MessageGridRow = null;
                    break;

                case "cmdline_block_show":
                    _cmdlineBlock.Clear();
                    foreach (var item in args)
                    {
                        var lines = Normalize(item).ElementAtOrDefault(0) as List<object?>;
                        if (lines is null) continue;
                        foreach (var line in lines)
                            _cmdlineBlock.Add(ChunksToText(line));
                    }
                    GuiLogger.Debug(GuiLogCategory.Cmdline, () => $"cmdline_block_show lines={_cmdlineBlock.Count}");
                    break;

                case "cmdline_block_append":
                    foreach (var item in args)
                        _cmdlineBlock.Add(ChunksToText(Normalize(item).ElementAtOrDefault(0)));
                    GuiLogger.Debug(GuiLogCategory.Cmdline, () => $"cmdline_block_append lines={_cmdlineBlock.Count}");
                    break;

                case "cmdline_block_hide":
                    _cmdlineBlock.Clear();
                    GuiLogger.Debug(GuiLogCategory.Cmdline, () => "cmdline_block_hide");
                    break;

                case "msg_clear":
                    _historyVisible = false;
                    _messages.Clear();
                    _messageEntries.Clear();
                    _historyEntries.Clear();
                    _cmdlineBlock.Clear();
                    ShowMode = string.Empty;
                    ShowCommand = string.Empty;
                    Ruler = string.Empty;
                    MessageGridId = null;
                    MessageGridRow = null;
                    _transientMessageGeneration++;
                    break;

                case "busy_start":
                    CursorVisible = false;
                    break;

                    case "busy_stop":
                        CursorVisible = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                GuiLogger.Error(GuiLogCategory.RedrawEvent, () => $"redraw handler failed name={name} error={ex}");
            }
        }

        Changed?.Invoke();
    }

    public void ClearTransientMessagesIfGeneration(int generation)
    {
        if (generation != _transientMessageGeneration || _messageEntries.Count == 0 || _historyVisible)
            return;

        _messages.Clear();
        _messageEntries.Clear();
        MessageGridId = null;
        MessageGridRow = null;
        _transientMessageGeneration++;
        GuiLogger.Debug(GuiLogCategory.Message, () => $"transient messages cleared generation={generation}");
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
        GuiLogger.Debug(GuiLogCategory.Resize, () => $"grid_resize grid={gridId} cols={cols} rows={rows}");
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
        if (row < 0 || row >= g.Rows || col < 0 || col >= g.Cols) return;
        if (a[3] is not List<object?> cells) return;

        var lastHl = col > 0 && col - 1 < g.Cols ? g.Cells[row][col - 1].Hl : 0;
        foreach (var cellObj in cells)
        {
            if (cellObj is not List<object?> cell || cell.Count == 0) continue;
            var text = Convert.ToString(NvimRpcClient.ToJsonable(cell[0]), CultureInfo.InvariantCulture) ?? string.Empty;
            var hl = cell.Count >= 2 && cell[1] is not null ? ToInt(cell[1]) : lastHl;
            var repeat = Math.Max(1, cell.Count >= 3 && cell[2] is not null ? ToInt(cell[2]) : 1);
            lastHl = hl;

            if (gridId == 1
                && row == g.Rows - 1
                && col > 0
                && col - 1 < g.Cols
                && g.Cells[row][col - 1].Continue)
            {
                var leadingRunes = text.EnumerateRunes().ToArray();
                if (leadingRunes.Length == 1 && leadingRunes[0].Value == ' ')
                {
                    repeat--;
                    if (repeat <= 0)
                        continue;
                }
                else if (leadingRunes.Length > 0 && leadingRunes[0].Value == ' ')
                {
                    text = string.Concat(leadingRunes.Skip(1).Select(r => r.ToString()));
                    if (text.Length == 0)
                        continue;
                }
            }

            var runes = text.EnumerateRunes().ToArray();
            for (var rep = 0; rep < repeat; rep++)
            {
                if (runes.Length == 0)
                {
                    SetCellSpan(g, row, ref col, " ", hl, 1);
                    continue;
                }

                for (var runeIndex = 0; runeIndex < runes.Length; runeIndex++)
                {
                    var rune = runes[runeIndex];
                    var ch = rune.ToString();
                    var span = IsWideRune(rune) ? 2 : 1;
                    SetCellSpan(g, row, ref col, ch, hl, span);

                    // Neovim's statusline row can already include a placeholder space
                    // after a wide glyph. If we also expand the glyph to 2 cells,
                    // that placeholder turns into an extra visible gap.
                    if (gridId == 1
                        && row == g.Rows - 1
                        && span == 2
                        && runeIndex + 1 < runes.Length
                        && runes[runeIndex + 1].Value == ' ')
                    {
                        runeIndex++;
                    }
                }
            }
        }

        if (gridId == 1 && row == g.Rows - 1)
        {
            CompactStatuslineWidePlaceholders(g, row);
            TraceStatuslineRow(g, row);
        }
    }

    private void ApplyScroll(object?[] a)
    {
        if (a.Length < 7) return;
        var gridId = ToInt(a[0]);
        if (!_grids.TryGetValue(gridId, out var g) || g.Rows == 0 || g.Cols == 0) return;
        var top = ToInt(a[1]);
        var bot = ToInt(a[2]);
        var left = ToInt(a[3]);
        var right = ToInt(a[4]);
        var delta = ToInt(a[5]);
        var colsDelta = ToInt(a[6]);
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
            for (var c = left; c < right; c++)
                CopyCell(g, r + d, c, r, c);

            for (var r = bot - d; r < bot; r++)
            for (var c = left; c < right; c++)
                ResetCell(g, r, c);
        }
        else
        {
            for (var r = bot - 1; r >= top + d; r--)
            for (var c = left; c < right; c++)
                CopyCell(g, r - d, c, r, c);

            for (var r = top; r < top + d; r++)
            for (var c = left; c < right; c++)
                ResetCell(g, r, c);
        }
    }

    private void SetCellSpan(GridState g, int row, ref int col, string ch, int hl, int span)
    {
        if (!InRange(g, row, col)) return;
        span = Math.Max(1, span);
        g.Cells[row][col].Ch = string.IsNullOrEmpty(ch) ? " " : ch;
        g.Cells[row][col].Hl = hl;
        g.Cells[row][col].Continue = false;

        for (var i = 1; i < span && col + i < g.Cols; i++)
        {
            g.Cells[row][col + i].Ch = string.Empty;
            g.Cells[row][col + i].Hl = hl;
            g.Cells[row][col + i].Continue = true;
        }

        col += span;
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

    private CursorModeState ResolveCursorMode(string modeName)
    {
        var mode = CursorModes.FirstOrDefault(m => string.Equals(m.Name, modeName, StringComparison.Ordinal));
        return mode ?? CursorModeState.ForMode(modeName);
    }

    private static PopupMenuState? ParsePopupMenuState(object?[] a, PopupMenuState? previous)
    {
        if (a.Length == 0) return previous;
        var items = new List<PopupMenuItem>();
        if (a.ElementAtOrDefault(0) is List<object?> rawItems)
        {
            foreach (var rawItem in rawItems)
            {
                var e = Normalize(rawItem);
                items.Add(new PopupMenuItem(
                    Decode(e.ElementAtOrDefault(0)),
                    Decode(e.ElementAtOrDefault(1)),
                    Decode(e.ElementAtOrDefault(2)),
                    Decode(e.ElementAtOrDefault(3))));
            }
        }

        return new PopupMenuState(
            items,
            ToInt(a.ElementAtOrDefault(1)),
            ToInt(a.ElementAtOrDefault(2)),
            ToInt(a.ElementAtOrDefault(3)),
            ToInt(a.ElementAtOrDefault(4)),
            previous?.Visible ?? true,
            items.Count > 0,
            ToInt(a.ElementAtOrDefault(4)) == -1 ? "Cmdline" : "Grid",
            ToInt(a.ElementAtOrDefault(4)) == -1);
    }

    private static TablineState ParseTablineState(object?[] a)
    {
        var currentTab = StableKey(a.ElementAtOrDefault(0));
        var tabsRaw = a.ElementAtOrDefault(1) as List<object?>;
        var currentBuffer = StableKey(a.ElementAtOrDefault(2));
        var buffersRaw = a.ElementAtOrDefault(3) as List<object?>;
        var buffers = new List<TablineBuffer>();
        var byBuffer = new Dictionary<string, TablineBuffer>(StringComparer.Ordinal);

        if (buffersRaw is not null)
        {
            foreach (var raw in buffersRaw)
            {
                var e = Normalize(raw);
                var entry = AsMap(e.ElementAtOrDefault(0));
                var id = StableKey(entry?.GetValueOrDefault("buffer") ?? e.ElementAtOrDefault(0));
                var name = Decode(entry?.GetValueOrDefault("name") ?? e.ElementAtOrDefault(1));
                var changed = ToBool(entry?.GetValueOrDefault("changed") ?? e.ElementAtOrDefault(2));
                var buffer = new TablineBuffer(id, name, changed);
                buffers.Add(buffer);
                byBuffer[id] = buffer;
            }
        }

        var tabs = new List<TablineTab>();
        if (tabsRaw is not null)
        {
            var index = 1;
            foreach (var raw in tabsRaw)
            {
                var e = Normalize(raw);
                var entry = AsMap(e.ElementAtOrDefault(0));
                var tabId = StableKey(entry?.GetValueOrDefault("tab") ?? e.ElementAtOrDefault(0));
                var explicitName = Decode(entry?.GetValueOrDefault("name") ?? e.ElementAtOrDefault(1));
                byBuffer.TryGetValue(tabId == currentTab ? currentBuffer : string.Empty, out var currentBufferInfo);
                tabs.Add(new TablineTab(
                    tabId,
                    index,
                    string.IsNullOrWhiteSpace(explicitName) ? (currentBufferInfo?.Name ?? $"tab {index}") : explicitName,
                    tabId == currentTab,
                    currentBufferInfo?.Changed == true));
                index++;
            }
        }

        return new TablineState(currentTab, currentBuffer, tabs, buffers);
    }

    private static IReadOnlyList<CursorModeState> ParseCursorModes(object? rawInfos)
    {
        var result = new List<CursorModeState>();
        if (rawInfos is not List<object?> infos) return result;

        foreach (var raw in infos)
        {
            var map = AsMap(raw);
            if (map is null) continue;
            var name = Decode(map.GetValueOrDefault("name"));
            if (string.IsNullOrWhiteSpace(name)) continue;
            var shape = Decode(map.GetValueOrDefault("cursor_shape"));
            var percentage = ToInt(map.GetValueOrDefault("cell_percentage"));
            if (percentage <= 0) percentage = shape == "block" ? 100 : 25;
            result.Add(new CursorModeState(name, NormalizeCursorShape(shape), percentage));
        }

        return result;
    }

    private static string NormalizeCursorShape(string shape)
    {
        return shape switch
        {
            "vertical" => "vertical",
            "horizontal" => "horizontal",
            _ => "block",
        };
    }

    private static bool InRange(GridState g, int row, int col) => row >= 0 && row < g.Rows && col >= 0 && col < g.Cols;

    private static bool IsWideRune(Rune rune)
    {
        var value = rune.Value;
        return value is
            >= 0x1100 and <= 0x115F or
            >= 0x2329 and <= 0x232A or
            >= 0x2E80 and <= 0xA4CF or
            >= 0xAC00 and <= 0xD7A3 or
            >= 0xF900 and <= 0xFAFF or
            >= 0xFE10 and <= 0xFE19 or
            >= 0xFE30 and <= 0xFE6F or
            >= 0xFF00 and <= 0xFF60 or
            >= 0xFFE0 and <= 0xFFE6 or
            >= 0x1F300 and <= 0x1FAFF or
            >= 0x20000 and <= 0x3FFFD;
    }

    private static void CompactStatuslineWidePlaceholders(GridState grid, int row)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var col = 0; col < grid.Cols - 3; col++)
            {
                var current = grid.Cells[row][col];
                var cont = grid.Cells[row][col + 1];
                var space = grid.Cells[row][col + 2];
                var next = grid.Cells[row][col + 3];

                if (current.Continue || string.IsNullOrEmpty(current.Ch) || current.Ch == " ")
                    continue;
                if (!cont.Continue)
                    continue;
                if (space.Continue || space.Ch != " " || space.Hl != current.Hl)
                    continue;
                if (next.Continue || string.IsNullOrEmpty(next.Ch) || next.Ch == " " || next.Hl != current.Hl)
                    continue;

                for (var shift = col + 2; shift < grid.Cols - 1; shift++)
                {
                    grid.Cells[row][shift].Ch = grid.Cells[row][shift + 1].Ch;
                    grid.Cells[row][shift].Hl = grid.Cells[row][shift + 1].Hl;
                    grid.Cells[row][shift].Continue = grid.Cells[row][shift + 1].Continue;
                }

                grid.Cells[row][grid.Cols - 1].Ch = " ";
                grid.Cells[row][grid.Cols - 1].Hl = 0;
                grid.Cells[row][grid.Cols - 1].Continue = false;
                changed = true;
                break;
            }
        }
    }

    private static void TraceStatuslineRow(GridState grid, int row)
    {
        if (!StatuslineDebugEnabled) return;
        try
        {
            var directory = Path.GetDirectoryName(StatuslineDebugPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var parts = new List<string>();
            for (var col = 0; col < grid.Cols; col++)
            {
                var cell = grid.Cells[row][col];
                var ch = cell.Continue ? "<cont>" : string.IsNullOrEmpty(cell.Ch) ? "<empty>" : cell.Ch.Replace("\r", "\\r").Replace("\n", "\\n");
                parts.Add($"{col}:{ch}:hl={cell.Hl}:cont={(cell.Continue ? 1 : 0)}");
            }

            var text = string.Concat(grid.Cells[row]
                .Where(c => !c.Continue && !string.IsNullOrEmpty(c.Ch))
                .Select(c => c.Ch));

            File.AppendAllText(
                StatuslineDebugPath,
                $"[{DateTime.Now:O}] text={text}{Environment.NewLine}{string.Join(" | ", parts)}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static object?[] Normalize(object? item) => item is List<object?> list ? list.ToArray() : [item];

    private static Dictionary<string, object?>? AsMap(object? value)
    {
        return value switch
        {
            Dictionary<object, object?> dict => dict.ToDictionary(kv => Decode(kv.Key), kv => kv.Value, StringComparer.Ordinal),
            Dictionary<string, object?> stringDict => new Dictionary<string, object?>(stringDict, StringComparer.Ordinal),
            _ => null
        };
    }

    private static string Decode(object? v) => NvimRpcClient.ToJsonable(v)?.ToString() ?? string.Empty;

    private static string StableKey(object? v)
    {
        var jsonable = NvimRpcClient.ToJsonable(v);
        return jsonable switch
        {
            null => string.Empty,
            string s => s,
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            _ => jsonable.ToString() ?? string.Empty
        };
    }

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

    private static IReadOnlyList<MessageChunk> NormalizeMessageChunks(object? chunks)
    {
        if (chunks is not List<object?> list)
        {
            var text = Decode(chunks);
            return string.IsNullOrEmpty(text) ? Array.Empty<MessageChunk>() : [new MessageChunk(text, 0)];
        }

        var result = new List<MessageChunk>();
        foreach (var item in list)
        {
            var chunk = Normalize(item);
            var hlId = ToInt(chunk.ElementAtOrDefault(0));
            var text = chunk.Length >= 2 ? Decode(chunk[1]) : Decode(chunk.ElementAtOrDefault(0));
            if (!string.IsNullOrEmpty(text))
                result.Add(new MessageChunk(text, hlId));
        }
        return result;
    }

    private static bool ToBool(object? v) => NvimRpcClient.ToJsonable(v) switch
    {
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        _ => false
    };

    private static int ToInt(object? v) => NvimRpcClient.ToJsonable(v) switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)Math.Round(d),
        float f => (int)Math.Round(f),
        string s when int.TryParse(s, out var n) => n,
        _ => 0
    };

    private static string Sanitize(string value)
        => value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string? ToHex(object? v) => NvimRpcClient.ToJsonable(v) switch
    {
        int i => $"#{i & 0xFFFFFF:X6}".ToLowerInvariant(),
        long l => $"#{l & 0xFFFFFF:X6}".ToLowerInvariant(),
        _ => null
    };
}

public sealed record CmdlineState(
    string Text,
    int Position,
    string FirstChar,
    string Prompt,
    int Indent,
    int Level,
    string SpecialChar,
    bool SpecialShift,
    IReadOnlyList<MessageChunk> Chunks);

public sealed record PopupMenuItem(string Word, string Kind, string Menu, string Info);

public sealed record MessageChunk(string Text, int HlId);

public sealed record MessageEntry(string Text, string Kind, IReadOnlyList<MessageChunk> Chunks, bool History);

public sealed record PopupMenuState(
    IReadOnlyList<PopupMenuItem> Items,
    int Selected,
    int Row,
    int Col,
    int Grid,
    bool Visible,
    bool HasItems,
    string AnchorKind,
    bool IsCmdline);

public sealed record TablineTab(string Id, int Index, string Label, bool Current, bool Changed);

public sealed record TablineBuffer(string Id, string Name, bool Changed);

public sealed record TablineState(
    string CurrentTab,
    string CurrentBuffer,
    IReadOnlyList<TablineTab> Tabs,
    IReadOnlyList<TablineBuffer> Buffers)
{
    public static TablineState Empty { get; } = new(string.Empty, string.Empty, Array.Empty<TablineTab>(), Array.Empty<TablineBuffer>());
}

public sealed record CursorModeState(string Name, string CursorShape, int CellPercentage)
{
    public static CursorModeState Default { get; } = new("normal", "block", 100);

    public static CursorModeState ForMode(string? modeName)
    {
        var mode = (modeName ?? string.Empty).ToLowerInvariant();
        if (mode.Contains("insert", StringComparison.Ordinal) || mode.StartsWith("i", StringComparison.Ordinal))
            return new CursorModeState(modeName ?? "insert", "vertical", 25);
        if (mode.Contains("replace", StringComparison.Ordinal) || mode.StartsWith("r", StringComparison.Ordinal))
            return new CursorModeState(modeName ?? "replace", "horizontal", 20);
        return new CursorModeState(modeName ?? "normal", "block", 100);
    }
}

public sealed class GridState(int id)
{
    public int Id { get; } = id;
    public int Rows { get; set; }
    public int Cols { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    public int RenderRow { get; set; }
    public int RenderCol { get; set; }
    public int AnchorGrid { get; set; } = 1;
    public int AnchorRow { get; set; }
    public int AnchorCol { get; set; }
    public string FloatAnchor { get; set; } = "NW";
    public bool Focusable { get; set; } = true;
    public int ZIndex { get; set; }
    public int EffectiveZIndex => Floating ? 1000 + ZIndex : ZIndex;
    public bool Visible { get; set; }
    public bool Floating { get; set; }
    public GridCell[][] Cells { get; set; } = Array.Empty<GridCell[]>();
}
