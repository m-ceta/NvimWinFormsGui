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
    private readonly Typeface _typeface = new("monospace");
    private double _fontSize = 14;
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

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_model is null) return;

        var bg = Brush.Parse(_model.DefaultBackground);
        context.FillRectangle(bg, Bounds);

        var grid = _model.Grid;
        for (var row = 0; row < grid.Length; row++)
        {
            var y = row * _cellHeight;
            for (var col = 0; col < grid[row].Length; col++)
            {
                var x = col * _cellWidth;
                var cell = grid[row][col];
                var style = _model.Highlights.TryGetValue(cell.Hl, out var hl) ? hl : null;
                if (!string.IsNullOrWhiteSpace(style?.Background))
                    context.FillRectangle(Brush.Parse(style.Background!), new Rect(x, y, _cellWidth, _cellHeight));

                if (!string.IsNullOrEmpty(cell.Ch) && cell.Ch != " ")
                {
                    var fg = style?.Foreground ?? _model.DefaultForeground;
                    var text = new FormattedText(
                        cell.Ch,
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        _fontSize,
                        Brush.Parse(fg));
                    context.DrawText(text, new Point(x, y));
                }
            }
        }

        if (_model.CursorRow >= 0 && _model.CursorCol >= 0)
        {
            context.DrawRectangle(null, new Pen(Brushes.White, 1),
                new Rect(_model.CursorCol * _cellWidth, _model.CursorRow * _cellHeight, _cellWidth, _cellHeight));
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
        var (row, col) = EventToGrid(e.GetPosition(this));
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _leftDown = true;
            await _editor.MouseAsync("left", "press", Modifiers(e.KeyModifiers), 0, row, col);
        }
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_editor is null || _model is null || !_leftDown) return;
        _leftDown = false;
        var (row, col) = EventToGrid(e.GetPosition(this));
        await _editor.MouseAsync("left", "release", Modifiers(e.KeyModifiers), 0, row, col);
    }

    protected override async void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_editor is null || _model is null || !_leftDown) return;
        var (row, col) = EventToGrid(e.GetPosition(this));
        await _editor.MouseAsync("left", "drag", Modifiers(e.KeyModifiers), 0, row, col);
    }

    protected override async void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_editor is null || _model is null) return;
        var (row, col) = EventToGrid(e.GetPosition(this));
        await _editor.MouseAsync("wheel", e.Delta.Y < 0 ? "down" : "up", Modifiers(e.KeyModifiers), 0, row, col);
    }

    private (int row, int col) EventToGrid(Point pos)
    {
        var rows = Math.Max(1, _model?.Rows ?? 1);
        var cols = Math.Max(1, _model?.Cols ?? 1);
        var row = Math.Max(0, Math.Min(rows - 1, (int)(pos.Y / _cellHeight)));
        var col = Math.Max(0, Math.Min(cols - 1, (int)(pos.X / _cellWidth)));
        return (row, col);
    }

    private async Task ResizeNvimAsync()
    {
        if (_editor is null) return;
        MeasureCell();
        var cols = Math.Max(2, (int)(Bounds.Width / _cellWidth));
        var rows = Math.Max(2, (int)(Bounds.Height / _cellHeight));
        await _editor.ResizeAsync(cols, rows);
    }

    private void MeasureCell()
    {
        var probe = new FormattedText("W", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _fontSize, Brushes.White);
        _cellWidth = Math.Max(1, probe.Width);
        _cellHeight = Math.Max(1, probe.Height + 2);
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
}
