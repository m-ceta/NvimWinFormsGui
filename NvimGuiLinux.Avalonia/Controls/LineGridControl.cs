using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using NvimGuiCommon.Editor;

namespace NvimGuiLinux.Avalonia.Controls;

public sealed class LineGridControl : Control
{
    private EditorController? _editor;
    private LineGridModel? _model;
    private readonly FontFamily _fontFamily = new("Consolas, monospace");
    private double _fontSize = 14;
    private double _lineHeight = 1.1;
    private double _cellWidth = 8;
    private double _cellHeight = 18;
    private bool _leftDown;

    public LineGridControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public void Bind(EditorController editor, LineGridModel model)
    {
        _editor = editor;
        _model = model;
        _model.Changed += () => Dispatcher.UIThread.Post(InvalidateVisual);
        SizeChanged += async (_, __) => await ResizeNvimAsync();
        MeasureCell();
        InvalidateVisual();
    }

    public Task ResizeNvimToBoundsAsync() => ResizeNvimAsync();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_model is null) return;

        var controlRect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.FillRectangle(Brush.Parse(_model.DefaultBackground), controlRect);

        foreach (var grid in _model.VisibleGrids)
            RenderGrid(context, grid);

        RenderMessages(context);
        if (_model.CursorRow >= 0 && _model.CursorCol >= 0)
        {
            var cursorGrid = _model.Grids.TryGetValue(_model.CursorGrid, out var g)
                ? g
                : _model.Grids.GetValueOrDefault(1);
            if (cursorGrid is not null)
            {
                context.DrawRectangle(
                    null,
                    new Pen(Brushes.White, 1),
                    new Rect(
                        (cursorGrid.Col + _model.CursorCol) * _cellWidth,
                        (cursorGrid.Row + _model.CursorRow) * _cellHeight,
                        _cellWidth,
                        _cellHeight));
            }
        }
    }

    private void RenderMessages(DrawingContext context)
    {
        if (_model is null) return;

        var activeCmdline = _model.ActiveCmdline;
        var showMode = _model.ShowMode;
        var showCommand = _model.ShowCommand;
        var messages = _model.Messages;
        var overlayLines = new List<string>();

        if (activeCmdline is not null)
        {
            var prefix = !string.IsNullOrEmpty(activeCmdline.Prompt)
                ? activeCmdline.Prompt
                : activeCmdline.FirstChar;
            overlayLines.Add($"{prefix}{activeCmdline.Text}");
        }
        else if (messages.Count > 0)
            overlayLines.AddRange(messages);
        if (activeCmdline is null && !string.IsNullOrWhiteSpace(showMode))
            overlayLines.Add(showMode);
        if (activeCmdline is null && !string.IsNullOrWhiteSpace(showCommand))
            overlayLines.Add(showCommand);

        if (overlayLines.Count == 0) return;

        var maxOverlayLines = 1;
        if (overlayLines.Count > maxOverlayLines)
            overlayLines = overlayLines.Skip(overlayLines.Count - maxOverlayLines).ToList();

        var startY = Math.Max(0, Bounds.Height - (overlayLines.Count * _cellHeight));
        var background = Brush.Parse(_model.DefaultBackground);
        var foreground = Brush.Parse(_model.DefaultForeground);
        var typeface = new Typeface(_fontFamily);

        context.FillRectangle(background, new Rect(0, startY, Bounds.Width, overlayLines.Count * _cellHeight));

        for (var i = 0; i < overlayLines.Count; i++)
        {
            var text = new FormattedText(
                overlayLines[i],
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                _fontSize,
                foreground);
            var y = startY + (i * _cellHeight) + Math.Round((_cellHeight - text.Height) / 2);
            context.DrawText(text, new Point(0, y));
        }

        if (activeCmdline is not null)
        {
            var cmdlineIndex = overlayLines.Count - 1;
            var prefixLen = !string.IsNullOrEmpty(activeCmdline.Prompt)
                ? activeCmdline.Prompt.Length
                : activeCmdline.FirstChar.Length;
            var cursorCol = Math.Max(0, prefixLen + activeCmdline.Position);
            context.DrawRectangle(
                null,
                new Pen(foreground, 1),
                new Rect(
                    cursorCol * _cellWidth,
                    startY + (cmdlineIndex * _cellHeight),
                    _cellWidth,
                    _cellHeight));
        }
    }

    private void RenderGrid(DrawingContext context, GridState grid)
    {
        for (var row = 0; row < grid.Cells.Length; row++)
        {
            var y = (grid.Row + row) * _cellHeight;
            if (y >= Bounds.Height) continue;
            if (y + _cellHeight <= 0) continue;

            for (var col = 0; col < grid.Cells[row].Length; col++)
            {
                var x = (grid.Col + col) * _cellWidth;
                if (x >= Bounds.Width) break;
                if (x + _cellWidth <= 0) continue;

                var cell = grid.Cells[row][col];
                var (_, bg, _) = GetStyle(cell.Hl);

                context.FillRectangle(
                    Brush.Parse(bg),
                    new Rect(x, y, _cellWidth, _cellHeight));
            }

            for (var col = grid.Cells[row].Length - 1; col >= 0; col--)
            {
                var x = (grid.Col + col) * _cellWidth;
                if (x >= Bounds.Width) continue;
                if (x + _cellWidth <= 0) continue;

                var cell = grid.Cells[row][col];
                if (string.IsNullOrEmpty(cell.Ch) || cell.Ch == " ")
                    continue;

                var (fg, bg, style) = GetStyle(cell.Hl);
                if (string.Equals(fg, bg, StringComparison.OrdinalIgnoreCase))
                    fg = _model?.DefaultForeground ?? "#d4d4d4";

                var text = new FormattedText(
                    cell.Ch,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    GetTypeface(style),
                    _fontSize,
                    Brush.Parse(fg));

                var textY = y + Math.Round((_cellHeight - text.Height) / 2);
                context.DrawText(text, new Point(x, textY));

                if (style?.Underline == true || style?.Undercurl == true)
                {
                    var underlineY = y + _cellHeight - 2;
                    context.DrawLine(new Pen(Brush.Parse(style.Special ?? fg), 1), new Point(x, underlineY), new Point(x + _cellWidth, underlineY));
                }

                if (style?.Strikethrough == true)
                {
                    var strikeY = y + (_cellHeight / 2);
                    context.DrawLine(new Pen(Brush.Parse(style.Special ?? fg), 1), new Point(x, strikeY), new Point(x + _cellWidth, strikeY));
                }
            }
        }
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_editor is null) return;
        var mapped = TranslateKey(e);
        if (mapped is null) return;
        e.Handled = true;
        await _editor.InputAsync(mapped.Value.data, mapped.Value.termcode);
    }

    protected override async void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_editor is null || string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        await _editor.InputAsync(e.Text, false);
    }

    protected override async void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_editor is null || _model is null) return;
        var (grid, row, col) = EventToGrid(e.GetPosition(this));
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _leftDown = true;
            await _editor.MouseAsync("left", "press", Modifiers(e.KeyModifiers), grid, row, col);
        }
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_editor is null || _model is null || !_leftDown) return;
        _leftDown = false;
        var (grid, row, col) = EventToGrid(e.GetPosition(this));
        await _editor.MouseAsync("left", "release", Modifiers(e.KeyModifiers), grid, row, col);
    }

    protected override async void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_editor is null || _model is null || !_leftDown) return;
        var (grid, row, col) = EventToGrid(e.GetPosition(this));
        await _editor.MouseAsync("left", "drag", Modifiers(e.KeyModifiers), grid, row, col);
    }

    protected override async void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_editor is null || _model is null) return;
        var (grid, row, col) = EventToGrid(e.GetPosition(this));
        await _editor.MouseAsync("wheel", e.Delta.Y < 0 ? "down" : "up", Modifiers(e.KeyModifiers), grid, row, col);
    }

    private (int grid, int row, int col) EventToGrid(Point pos)
    {
        if (_model is not null)
        {
            foreach (var grid in _model.VisibleGrids.Reverse())
            {
                var left = grid.Col * _cellWidth;
                var top = grid.Row * _cellHeight;
                var right = left + (grid.Cols * _cellWidth);
                var bottom = top + (grid.Rows * _cellHeight);
                if (pos.X < left || pos.X >= right || pos.Y < top || pos.Y >= bottom)
                    continue;

                var row = Math.Max(0, Math.Min(grid.Rows - 1, (int)((pos.Y - top) / _cellHeight)));
                var col = Math.Max(0, Math.Min(grid.Cols - 1, (int)((pos.X - left) / _cellWidth)));
                return (grid.Id, row, col);
            }
        }

        return (1, 0, 0);
    }

    private async Task ResizeNvimAsync()
    {
        if (_editor is null) return;
        MeasureCell();
        var cols = Math.Max(2, (int)(Bounds.Width / _cellWidth));
        var rows = Math.Max(2, (int)(Bounds.Height / _cellHeight) - 1);
        await _editor.ResizeAsync(cols, rows);
    }

    private void MeasureCell()
    {
        var probe = new FormattedText("W", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(_fontFamily), _fontSize, Brushes.White);
        _cellWidth = Math.Max(1, Math.Ceiling(probe.Width));
        _cellHeight = Math.Max(1, Math.Ceiling(_fontSize * _lineHeight));
    }

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
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && !string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol!.Length == 1)
            return ($"<C-{e.KeySymbol.ToLowerInvariant()}>", true);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol!.Length == 1)
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

    private Typeface GetTypeface(HighlightStyle? style)
    {
        var fontStyle = style?.Italic == true ? FontStyle.Italic : FontStyle.Normal;
        var fontWeight = style?.Bold == true ? FontWeight.Bold : FontWeight.Normal;
        return new Typeface(_fontFamily, fontStyle, fontWeight);
    }

    private (string fg, string bg, HighlightStyle? style) GetStyle(int hlId)
    {
        HighlightStyle? style = null;
        if (_model is not null)
            _model.Highlights.TryGetValue(hlId, out style);

        var fg = !string.IsNullOrWhiteSpace(style?.Foreground)
            ? style!.Foreground!
            : _model?.DefaultForeground ?? "#d4d4d4";

        var bg = !string.IsNullOrWhiteSpace(style?.Background)
            ? style!.Background!
            : _model?.DefaultBackground ?? "#1e1e1e";

        if (style?.Reverse == true)
            (fg, bg) = (bg, fg);

        return (fg, bg, style);
    }
}
