using Avalonia;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Threading;
using NvimGuiCommon.Diagnostics;
using NvimGuiCommon.Editor;
using System.Text;

namespace NvimGuiLinux.Avalonia.Controls;

public sealed class LineGridControl : EditorLayerControl
{
    private EditorController? _editor;
    private CancellationTokenSource? _resizeCts;
    private bool _leftDown;
    private string _preeditText = string.Empty;
    private int? _preeditCursorOffset;
    private Rect _lastImeCursorRect;
    private bool _hasLastImeCursorRect;
    private readonly List<TabHitTarget> _tabHitTargets = new();
    private readonly NvimTextInputMethodClient _textInputMethodClient;

    public LineGridControl()
    {
        _textInputMethodClient = new NvimTextInputMethodClient(this);
        Focusable = true;
        IsHitTestVisible = true;
        TextInputOptions.SetContentType(this, TextInputContentType.Normal);
        TextInputMethodClientRequested += (_, e) =>
        {
            GuiLogger.Debug(GuiLogCategory.TextInput, () => $"TextInputMethodClientRequested cursor={FormatRect(_textInputMethodClient.CursorRectangle)}");
            e.Client = _textInputMethodClient;
        };
        SizeChanged += (_, _) => ScheduleResize();
        GotFocus += (_, _) =>
        {
            GuiLogger.Info(GuiLogCategory.Focus, () => "EditorGrid focus gained");
            GuiLogger.Debug(GuiLogCategory.TextInput, () => $"EditorGrid focus cursor={FormatRect(_textInputMethodClient.CursorRectangle)}");
        };
        LostFocus += (_, _) =>
        {
            ClearPreedit();
            GuiLogger.Info(GuiLogCategory.Focus, () => "EditorGrid focus lost");
        };
    }

    public void Bind(EditorController editor, LineGridModel model)
    {
        _editor = editor;
        base.Bind(model);
        ScheduleResize();
    }

    public Task ResizeNvimToBoundsAsync() => ResizeNvimAsync(CancellationToken.None);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Model is null)
            return;

        var layout = CreateLayoutSnapshot();
        context.FillRectangle(ToBrush(Model.DefaultBackground), new Rect(0, 0, Bounds.Width, Bounds.Height));

        foreach (var grid in Model.VisibleGrids.Where(g => !g.Floating))
        {
            GuiLogger.Debug(
                GuiLogCategory.Render,
                () => $"GridRender grid={grid.Id} row={grid.Row} col={grid.Col} rows={grid.Rows} cols={grid.Cols} tail={DescribeTailRows(grid, 3)}");
            RenderGrid(context, grid, drawShadow: false);
        }

        RenderTabline(context, layout);

        if (Model.Grids.TryGetValue(Model.CursorGrid, out var cursorGrid) && cursorGrid.Visible && !cursorGrid.Floating)
            RenderCursorOnGrid(context, cursorGrid);

        RenderPreedit(context);
        PublishImeCursorRectIfChanged();
    }

    protected override bool IsSpecialGridRow(GridState grid, int row)
    {
        if (Model is null || grid.Id != 1 || grid.Rows != Model.Rows)
            return false;

        var statuslineRow = GetStatuslineRow(grid);
        return row == statuslineRow;
    }

    protected override void RenderSpecialGridRow(DrawingContext context, GridState grid, int row, double left, double y)
    {
        if (row >= 0 && row < grid.Cells.Length)
        {
            var text = string.Concat(grid.Cells[row].Where(cell => !cell.Continue && !string.IsNullOrEmpty(cell.Ch)).Select(cell => cell.Ch));
            GuiLogger.Debug(GuiLogCategory.Render, () => $"GridStatusLine text={text} runs={DescribeResolvedTextRuns(text, null)}");
            GuiLogger.Debug(GuiLogCategory.Render, () => $"GridStatusLine cells={DescribeLeadingCells(grid, row, 24)}");
        }

        for (var col = 0; col < grid.Cells[row].Length; col++)
        {
            var x = left + (col * CellWidth);
            if (x >= Bounds.Width)
                break;
            if (x + CellWidth <= 0)
                continue;

            var cell = grid.Cells[row][col];
            var (_, bg, _) = GetStyle(cell.Hl);
            context.FillRectangle(ToBrush(bg), new Rect(x, y, CellWidth, CellHeight));
        }

        DrawSpecialGridRowForeground(context, grid, row, left, y);
    }

    protected override void OnMetricsChanged()
    {
        base.OnMetricsChanged();
        ScheduleResize();
    }

    protected override async void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_editor is null || string.IsNullOrEmpty(e.Text))
            return;

        if (e.Text.All(char.IsControl))
            return;

        GuiLogger.Debug(GuiLogCategory.TextInput, () => $"TextInput text={Sanitize(e.Text)} handled_before={e.Handled}");
        ClearPreedit();
        await _editor.InputAsync(e.Text, false);
        e.Handled = true;
        GuiLogger.Debug(GuiLogCategory.TextInput, () => $"TextInput text={Sanitize(e.Text)} handled_after={e.Handled}");
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_editor is null)
            return;

        var mapped = TranslateKey(e);
        GuiLogger.Debug(
            GuiLogCategory.Keyboard,
            () => $"KeyDown key={e.Key} symbol={e.KeySymbol} modifiers={e.KeyModifiers} mapped={(mapped?.data ?? "<none>")} termcode={(mapped?.termcode ?? false)} handled_before={e.Handled}");

        if (mapped is null)
        {
            if (!string.IsNullOrEmpty(e.KeySymbol))
            {
                GuiLogger.Debug(
                    GuiLogCategory.TextInput,
                    () => $"KeyDown passthrough key={e.Key} symbol={Sanitize(e.KeySymbol)} modifiers={e.KeyModifiers} handled={e.Handled}");
            }
            return;
        }

        e.Handled = true;
        await _editor.InputAsync(mapped.Value.data, mapped.Value.termcode);

        GuiLogger.Debug(
            GuiLogCategory.Keyboard,
            () => $"KeyDown key={e.Key} symbol={e.KeySymbol} modifiers={e.KeyModifiers} mapped={mapped.Value.data} termcode={mapped.Value.termcode} handled_after={e.Handled}");
    }

    protected override async void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var tabHit = HitTestTabline(e.GetPosition(this));
        if (tabHit is not null && _editor is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            if (tabHit.CloseButton)
                await _editor.CloseTabAsync(tabHit.Index);
            else
                await _editor.SwitchTabAsync(tabHit.Index);
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
            _leftDown = true;

        await SendMouseAsync(e, point.Properties);
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        await SendMouseAsync(e, e.GetCurrentPoint(this).Properties);
        _leftDown = false;
    }

    protected override async void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_leftDown || _editor is null)
            return;

        var hit = EventToGrid(e.GetPosition(this));
        GuiLogger.Debug(
            GuiLogCategory.Mouse,
            () => $"Mouse raw={e.GetPosition(this)} matched_grid={hit.grid} rect={FormatRect(hit.rect)} local_row={hit.row} local_col={hit.col} button=left action=drag modifiers={e.KeyModifiers}");
        await _editor.MouseAsync("left", "drag", Modifiers(e.KeyModifiers), hit.grid, hit.row, hit.col);
    }

    protected override async void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_editor is null)
            return;

        var hit = EventToGrid(e.GetPosition(this));
        var action = e.Delta.Y < 0 ? "down" : "up";
        GuiLogger.Debug(
            GuiLogCategory.Mouse,
            () => $"Mouse raw={e.GetPosition(this)} matched_grid={hit.grid} rect={FormatRect(hit.rect)} local_row={hit.row} local_col={hit.col} button=wheel action={action} modifiers={e.KeyModifiers}");
        await _editor.MouseAsync("wheel", action, Modifiers(e.KeyModifiers), hit.grid, hit.row, hit.col);
    }

    private async Task SendMouseAsync(PointerEventArgs e, PointerPointProperties properties)
    {
        if (_editor is null)
            return;

        var hit = EventToGrid(e.GetPosition(this));
        var (button, action) = GetMouseButtonAction(e, properties);
        if (button is null || action is null)
            return;

        GuiLogger.Debug(
            GuiLogCategory.Mouse,
            () => $"Mouse raw={e.GetPosition(this)} matched_grid={hit.grid} rect={FormatRect(hit.rect)} local_row={hit.row} local_col={hit.col} button={button} action={action} modifiers={e.KeyModifiers}");
        await _editor.MouseAsync(button, action, Modifiers(e.KeyModifiers), hit.grid, hit.row, hit.col);
    }

    private (int grid, int row, int col, Rect rect) EventToGrid(Point pos)
    {
        if (Model is null)
            return (1, 0, 0, default);

        if (Model.PopupMenuState is { HasItems: true } popup)
        {
            var layout = CalculatePopupMenuLayout(popup, GetCmdlineRect(), 0, -1);
            if (layout.Rect.Contains(pos))
            {
                var row = Math.Clamp((int)((pos.Y - layout.Rect.Y) / Math.Max(1, CellHeight)), 0, popup.Items.Count - 1);
                var localRow = popup.Row + row;
                return (popup.Grid, localRow, popup.Col, layout.Rect);
            }
        }

        if (GetCmdlineRect() is { } cmdlineRect && cmdlineRect.Contains(pos))
        {
            var col = Math.Max(0, Math.Min(Math.Max(0, Model.Cols - 1), (int)((pos.X - cmdlineRect.X) / Math.Max(1, CellWidth))));
            return (-1, 0, col, cmdlineRect);
        }

        var grids = Model.VisibleGrids
            .OrderByDescending(g => g.EffectiveZIndex)
            .ThenByDescending(g => g.Floating)
            .ThenByDescending(g => g.Id);

        foreach (var grid in grids)
        {
            var rect = GetGridRect(grid);
            if (!rect.Contains(pos))
                continue;

            var row = Math.Max(0, Math.Min(grid.Rows - 1, (int)((pos.Y - rect.Y) / Math.Max(1, CellHeight))));
            var col = Math.Max(0, Math.Min(grid.Cols - 1, (int)((pos.X - rect.X) / Math.Max(1, CellWidth))));
            return (grid.Id, row, col, rect);
        }

        return (1, 0, 0, default);
    }

    private void ScheduleResize()
    {
        _resizeCts?.Cancel();
        _resizeCts?.Dispose();
        _resizeCts = new CancellationTokenSource();
        var token = _resizeCts.Token;
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(60, token);
                await ResizeNvimAsync(token);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task ResizeNvimAsync(CancellationToken cancellationToken)
    {
        if (_editor is null || Model is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var cols = Math.Max(2, (int)Math.Floor(Bounds.Width / Math.Max(1, CellWidth)));
        // ext_cmdline/statusline are drawn from Neovim grid state and overlay controls,
        // so reserving an extra bottom row here creates a visible gap.
        var reservedBottomRows = 0;
        var rows = Math.Max(
            2,
            (int)Math.Floor(Bounds.Height / Math.Max(1, CellHeight))
            - Math.Max(0, Model.EditorTopOffset)
            - reservedBottomRows);

        GuiLogger.Info(
            GuiLogCategory.Resize,
            () => $"LineGridControl bounds={FormatRect(Bounds)} cellWidth={CellWidth:F2} cellHeight={CellHeight:F2} calcCols={cols} calcRows={rows} editorTopOffset={Model.EditorTopOffset} reservedBottomRows={reservedBottomRows}");
        await _editor.ResizeAsync(cols, rows);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static (string? button, string? action) GetMouseButtonAction(PointerEventArgs e, PointerPointProperties properties)
    {
        if (e is PointerReleasedEventArgs released)
        {
            return released.InitialPressMouseButton switch
            {
                MouseButton.Left => ("left", "release"),
                MouseButton.Right => ("right", "release"),
                MouseButton.Middle => ("middle", "release"),
                _ => (null, null)
            };
        }

        if (properties.IsLeftButtonPressed)
            return ("left", "press");
        if (properties.IsRightButtonPressed)
            return ("right", "press");
        if (properties.IsMiddleButtonPressed)
            return ("middle", "press");
        return (null, null);
    }

    private static string Sanitize(string value)
        => value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string FormatRect(Rect rect)
        => $"x={rect.X:F1},y={rect.Y:F1},w={rect.Width:F1},h={rect.Height:F1}";

    private static string Modifiers(KeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("S");
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("C");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("M");
        return string.Join('-', parts);
    }

    private static (string data, bool termcode)? TranslateKey(KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && !string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol.Length == 1)
            return ($"<C-{e.KeySymbol.ToLowerInvariant()}>", true);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol.Length == 1)
            return ($"<M-{e.KeySymbol.ToLowerInvariant()}>", true);

        return e.Key switch
        {
            Key.Enter => ("<CR>", true),
            Key.Back => ("<BS>", true),
            Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => ("<S-Tab>", true),
            Key.Tab => ("<Tab>", true),
            Key.Escape => ("<Esc>", true),
            Key.Left => ("<Left>", true),
            Key.Right => ("<Right>", true),
            Key.Up => ("<Up>", true),
            Key.Down => ("<Down>", true),
            Key.Home => ("<Home>", true),
            Key.End => ("<End>", true),
            Key.PageUp => ("<PageUp>", true),
            Key.PageDown => ("<PageDown>", true),
            Key.Delete => ("<Del>", true),
            Key.Insert => ("<Insert>", true),
            _ => null,
        };
    }

    private void RenderTabline(DrawingContext context, EditorLayoutSnapshot layout)
    {
        if (Model is null || Model.TablineState.Tabs.Count == 0)
            return;

        _tabHitTargets.Clear();
        var topInset = layout.EditorTopInset;
        var rect = new Rect(0, 0, Bounds.Width, Math.Max(1, topInset));
        context.FillRectangle(ToBrush(BlendColor(Model.DefaultBackground, "#ffffff", 0.06)), rect);
        context.DrawLine(new Pen(ToBrush("#ffffff1f"), 1), rect.BottomLeft, rect.BottomRight);

        var x = 6d;
        foreach (var tab in Model.TablineState.Tabs)
        {
            var fullLabel = string.IsNullOrWhiteSpace(tab.Label) ? $"tab {tab.Index}" : tab.Label;
            var badge = GetTabBadge(fullLabel);
            var label = GetTabDisplayLabel(fullLabel);
            var badgeText = MeasureOverlayText(badge);
            var labelText = MeasureOverlayText(label);
            var closeText = MeasureOverlayText("x");
            var width = Math.Min(Bounds.Width - x - 6, badgeText.Width + labelText.Width + closeText.Width + 34);
            if (width <= CellWidth)
                break;

            var tabRect = new Rect(x, 1, width, Math.Max(1, rect.Height - 4));
            var background = tab.Current
                ? BlendColor(Model.DefaultBackground, "#ffffff", 0.20)
                : BlendColor(Model.DefaultBackground, "#ffffff", 0.08);
            context.FillRectangle(ToBrush(background), tabRect, 4);

            var badgeBg = ToBrush(BlendColor(background, "#ffffff", 0.10));
            var badgeRect = new Rect(tabRect.X + 8, tabRect.Y + 1, Math.Max(16, badgeText.Width + 8), Math.Max(1, tabRect.Height - 2));
            context.FillRectangle(badgeBg, badgeRect, 999);
            DrawOverlayText(context, badge, new Point(badgeRect.X + 4, tabRect.Y), ToBrush(Model.DefaultForeground), opacity: 0.85, xInset: 0);

            var labelPrefix = tab.Changed ? $"{label} +" : label;
            DrawOverlayText(context, labelPrefix, new Point(badgeRect.Right + 8, tabRect.Y), ToBrush(Model.DefaultForeground), xInset: 0);
            var closeRect = new Rect(tabRect.Right - closeText.Width - 10, tabRect.Y, closeText.Width + 8, tabRect.Height);
            DrawOverlayText(context, "x", new Point(closeRect.X + 2, tabRect.Y), ToBrush(Model.DefaultForeground), opacity: 0.7, xInset: 0);
            _tabHitTargets.Add(new TabHitTarget(tab.Index, false, tabRect));
            _tabHitTargets.Add(new TabHitTarget(tab.Index, true, closeRect));
            x += width + 4;
            if (x >= Bounds.Width - CellWidth)
                break;
        }
    }

    private void RenderStatuslineRow(DrawingContext context, GridState grid, int row, double left, double y)
    {
        var startCol = -1;
        var endCol = -1;
        var currentHl = int.MinValue;

        void Flush()
        {
            if (startCol < 0 || endCol < startCol)
                return;

            var text = BuildStatuslineText(grid, row, startCol, endCol);
            if (string.IsNullOrEmpty(text))
            {
                startCol = -1;
                endCol = -1;
                currentHl = int.MinValue;
                return;
            }

            var x = left + (startCol * CellWidth);
            var width = Math.Max(1, ((endCol - startCol + 1) * CellWidth));
            var (fg, bg, style) = GetStyle(currentHl);
            DrawStatuslineRun(context, x, y, width, text, fg, bg, style);
            startCol = -1;
            endCol = -1;
            currentHl = int.MinValue;
        }

        for (var col = 0; col < grid.Cols; col++)
        {
            var cell = grid.Cells[row][col];
            if (cell.Continue)
                continue;

            if (startCol < 0)
            {
                startCol = col;
                endCol = col;
                currentHl = cell.Hl;
                continue;
            }

            if (cell.Hl != currentHl)
            {
                Flush();
                startCol = col;
                endCol = col;
                currentHl = cell.Hl;
                continue;
            }

            endCol = col;
        }

        Flush();
    }

    private string BuildStatuslineText(GridState grid, int row, int startCol, int endCol)
    {
        var chars = new List<string>(endCol - startCol + 1);
        for (var col = startCol; col <= endCol && col < grid.Cols; col++)
        {
            var cell = grid.Cells[row][col];
            if (cell.Continue)
                continue;
            chars.Add(string.IsNullOrEmpty(cell.Ch) ? " " : cell.Ch);
        }

        return string.Concat(chars);
    }

    private void RenderPlainRow(DrawingContext context, GridState grid, int row, double left, double y)
    {
        for (var col = 0; col < grid.Cells[row].Length; col++)
        {
            var x = left + (col * CellWidth);
            if (x >= Bounds.Width)
                break;
            if (x + CellWidth <= 0)
                continue;

            var cell = grid.Cells[row][col];
            var (_, bg, _) = GetStyle(cell.Hl);
            context.FillRectangle(ToBrush(bg), new Rect(x, y, CellWidth, CellHeight));
        }

        for (var col = 0; col < grid.Cells[row].Length; col++)
        {
            var cell = grid.Cells[row][col];
            if (cell.Continue || string.IsNullOrEmpty(cell.Ch) || cell.Ch == " ")
                continue;

            var x = left + (col * CellWidth);
            var width = GetCellSpan(grid, row, col) * CellWidth;
            if (x >= Bounds.Width)
                break;
            if (x + width <= 0)
                continue;

            var (fg, _, style) = GetStyle(cell.Hl);
            DrawCellForeground(context, x, y, cell.Ch, fg, style, width);
        }
    }

    private void DrawStatuslineRun(DrawingContext context, double x, double y, double width, string text, string foreground, string background, HighlightStyle? style)
    {
        if (string.IsNullOrEmpty(text))
            return;

        context.FillRectangle(ToBrush(background), new Rect(x, y, width, CellHeight));
        var brush = ToBrush(foreground);
        var measurement = MeasureResolvedTextRuns(text, style);
        var leftPadding = 3d;
        var drawX = Math.Max(0, Math.Min(x - leftPadding, Math.Max(0, Bounds.Width - 1)));
        var drawY = y + Math.Round((CellHeight - measurement.height) / 2);
        var clipWidth = Math.Max(width + 12, measurement.width + leftPadding + 6);
        var clipRect = new Rect(drawX, y, Math.Max(1, Math.Min(clipWidth, Bounds.Width - drawX)), CellHeight);
        using (context.PushClip(clipRect))
            DrawResolvedTextRuns(context, text, style, brush, new Point(drawX + leftPadding, drawY));
        DrawCellDecorations(context, drawX, y, width, foreground, style);
    }

    private TabHitTarget? HitTestTabline(Point pos)
    {
        if (Model is null || Model.TablineState.Tabs.Count == 0)
            return null;
        if (pos.Y < 0 || pos.Y > CellHeight)
            return null;

        return _tabHitTargets
            .OrderByDescending(t => t.CloseButton)
            .FirstOrDefault(t => t.Rect.Contains(pos));
    }

    private static string GetTabDisplayLabel(string fullLabel)
    {
        var text = (fullLabel ?? string.Empty).Trim();
        if (text.Length <= 20)
            return text;
        return $"...{text[^20..]}";
    }

    private static string GetTabBadge(string fullLabel)
    {
        var text = (fullLabel ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "tab";
        if (text.StartsWith('[') && text.EndsWith(']'))
            return "buf";
        var lower = text.ToLowerInvariant();
        if (lower.EndsWith("/") || lower.EndsWith("\\"))
            return "dir";
        var parts = lower.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var name = parts.Length == 0 ? lower : parts[^1];
        var dot = name.LastIndexOf('.');
        if (dot > 0 && dot < name.Length - 1)
            return name[(dot + 1)..Math.Min(name.Length, dot + 4)];
        return name[..Math.Min(3, name.Length)];
    }

    private static string DescribeLeadingCells(GridState grid, int row, int maxCells)
    {
        if (row < 0 || row >= grid.Cells.Length)
            return string.Empty;

        var parts = new List<string>(maxCells);
        var cells = grid.Cells[row];
        for (var col = 0; col < cells.Length && col < maxCells; col++)
        {
            var cell = cells[col];
            var text = cell.Continue
                ? "<cont>"
                : string.IsNullOrEmpty(cell.Ch)
                    ? "<empty>"
                    : Sanitize(cell.Ch).Replace(" ", "<sp>", StringComparison.Ordinal);
            var codepoints = cell.Continue || string.IsNullOrEmpty(cell.Ch)
                ? string.Empty
                : string.Join(",", cell.Ch.EnumerateRunes().Select(r => $"U+{r.Value:X4}"));
            parts.Add($"[{col}:{text}|hl={cell.Hl}|cp={codepoints}]");
        }

        return string.Join(" ", parts);
    }

    private static string DescribeRowText(GridState grid, int row)
    {
        if (row < 0 || row >= grid.Cells.Length)
            return string.Empty;

        var text = string.Concat(
            grid.Cells[row]
                .Where(cell => !cell.Continue)
                .Select(cell => string.IsNullOrEmpty(cell.Ch) ? " " : cell.Ch));

        return Sanitize(text).Replace(" ", "<sp>", StringComparison.Ordinal);
    }

    private static string DescribeTailRows(GridState grid, int count)
    {
        if (grid.Rows <= 0)
            return string.Empty;

        var start = Math.Max(0, grid.Rows - count);
        var parts = new List<string>(count);
        for (var row = start; row < grid.Rows; row++)
            parts.Add($"{row}:{DescribeRowText(grid, row)}");
        return string.Join(" || ", parts);
    }

    private void DrawSpecialGridRowForeground(DrawingContext context, GridState grid, int row, double left, double y)
    {
        RenderPlainRow(context, grid, row, left, y);
    }

    private int GetStatuslineRow(GridState grid)
    {
        var lastRow = grid.Rows - 1;
        if (lastRow <= 0)
            return lastRow;

        return HasRenderableContent(grid, lastRow) ? lastRow : lastRow - 1;
    }

    private static bool HasRenderableContent(GridState grid, int row)
    {
        if (row < 0 || row >= grid.Cells.Length)
            return false;

        foreach (var cell in grid.Cells[row])
        {
            if (cell.Continue)
                continue;
            if (!string.IsNullOrWhiteSpace(cell.Ch))
                return true;
        }

        return false;
    }

    private void RenderPreedit(DrawingContext context)
    {
        if (string.IsNullOrEmpty(_preeditText) || Model is null)
            return;

        if (!TryGetPreeditStartRect(out var preeditStartRect))
            return;

        var widthInCells = Math.Max(1, CountDisplayCells(_preeditText));
        var preeditRect = new Rect(
            preeditStartRect.X,
            preeditStartRect.Y,
            Math.Min(Bounds.Width - preeditStartRect.X, widthInCells * CellWidth),
            CellHeight);

        var background = BlendColor(Model.DefaultBackground, "#ffffff", 0.10);
        var foreground = Model.DefaultForeground;
        context.FillRectangle(ToBrush(background), preeditRect);

        var formatted = CreateFormattedText(_preeditText, style: null, ToBrush(foreground));
        var textY = preeditRect.Y + Math.Round((CellHeight - formatted.Height) / 2);
        using (context.PushClip(preeditRect))
            context.DrawText(formatted, new Point(preeditRect.X, textY));

        var underlineY = preeditRect.Bottom - 2;
        context.DrawLine(new Pen(ToBrush(foreground), 1), new Point(preeditRect.X, underlineY), new Point(preeditRect.Right, underlineY));

        var caretX = preeditRect.X + (CountDisplayCells(GetPreeditPrefix()) * CellWidth);
        context.DrawLine(new Pen(ToBrush(foreground), 1), new Point(caretX, preeditRect.Y + 1), new Point(caretX, preeditRect.Bottom - 1));
    }

    private bool TryGetPreeditStartRect(out Rect rect)
    {
        rect = default;
        if (Model is null || !Model.Grids.TryGetValue(Model.CursorGrid, out var cursorGrid) || !cursorGrid.Visible)
            return false;

        var gridLeft = GetGridLeft(cursorGrid);
        var gridTop = GetGridTop(cursorGrid);
        rect = new Rect(gridLeft + (Model.CursorCol * CellWidth), gridTop + (Model.CursorRow * CellHeight), CellWidth, CellHeight);
        return true;
    }

    private Rect GetImeCaretRect()
    {
        if (Model is null || !Model.Grids.TryGetValue(Model.CursorGrid, out var cursorGrid) || !cursorGrid.Visible)
            return default;

        var gridLeft = GetGridLeft(cursorGrid);
        var gridTop = GetGridTop(cursorGrid);
        var col = Model.CursorCol + CountDisplayCells(GetPreeditPrefix());
        return new Rect(
            gridLeft + (col * CellWidth),
            gridTop + CellHeight + (Model.CursorRow * CellHeight),
            Math.Max(1, Math.Round(CellWidth * 0.14d)),
            1);
    }

    private void PublishImeCursorRectIfChanged()
    {
        var rect = GetImeCaretRect();
        if (_hasLastImeCursorRect && rect == _lastImeCursorRect)
            return;

        _lastImeCursorRect = rect;
        _hasLastImeCursorRect = true;
        _textInputMethodClient.NotifyCursorRectangleChanged();
    }

    private void UpdatePreedit(string? text, int? cursorOffset)
    {
        _preeditText = text ?? string.Empty;
        _preeditCursorOffset = cursorOffset;
        GuiLogger.Debug(GuiLogCategory.TextInput, () => $"Preedit text={Sanitize(_preeditText)} cursorOffset={_preeditCursorOffset?.ToString() ?? "<null>"}");
        PublishImeCursorRectIfChanged();
        InvalidateVisual();
    }

    private void ClearPreedit()
    {
        if (string.IsNullOrEmpty(_preeditText) && _preeditCursorOffset is null)
            return;

        _preeditText = string.Empty;
        _preeditCursorOffset = null;
        GuiLogger.Debug(GuiLogCategory.TextInput, () => "Preedit cleared");
        PublishImeCursorRectIfChanged();
        InvalidateVisual();
    }

    private string GetPreeditPrefix()
    {
        if (string.IsNullOrEmpty(_preeditText) || _preeditCursorOffset is null)
            return string.Empty;

        var clamped = Math.Clamp(_preeditCursorOffset.Value, 0, _preeditText.Length);
        return _preeditText[..clamped];
    }

    private static int CountDisplayCells(string text)
    {
        var cells = 0;
        foreach (var rune in text.EnumerateRunes())
            cells += IsWideRune(rune) ? 2 : 1;
        return cells;
    }

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

    private sealed record TabHitTarget(int Index, bool CloseButton, Rect Rect);

    private sealed class NvimTextInputMethodClient(LineGridControl owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;

        public override bool SupportsPreedit => true;

        public override bool SupportsSurroundingText => false;

        public override string SurroundingText => string.Empty;

        public override TextSelection Selection { get; set; } = default;

        public override Rect CursorRectangle
        {
            get
            {
                var row = owner.Model?.CursorRow ?? 0;
                var col = (owner.Model?.CursorCol ?? 0) + CountDisplayCells(owner.GetPreeditPrefix());
                var rect = owner.GetImeCaretRect();
                GuiLogger.Debug(GuiLogCategory.TextInput, () => $"IME cursor row={row} col={col} rect={FormatRect(rect)}");
                return rect;
            }
        }

        public void NotifyCursorRectangleChanged() => RaiseCursorRectangleChanged();

        public override void SetPreeditText(string? preeditText)
            => owner.UpdatePreedit(preeditText, null);

        public override void SetPreeditText(string? preeditText, int? cursorPosition)
            => owner.UpdatePreedit(preeditText, cursorPosition);
    }
}
