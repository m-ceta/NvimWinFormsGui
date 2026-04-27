using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using NvimGuiCommon.Editor;
using System.Text;

namespace NvimGuiLinux.Avalonia.Controls;

public sealed class LineGridControl : Control
{
    public static readonly StyledProperty<string> FontFamilyNameProperty =
        AvaloniaProperty.Register<LineGridControl, string>(nameof(FontFamilyName), "Noto Sans Mono CJK JP, DejaVu Sans Mono, Noto Sans Mono, monospace");

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<LineGridControl, double>(nameof(FontSize), 14d);

    public static readonly StyledProperty<double> LineHeightProperty =
        AvaloniaProperty.Register<LineGridControl, double>(nameof(LineHeight), 1.1d);

    public static readonly StyledProperty<bool> UseFixedCellMetricsProperty =
        AvaloniaProperty.Register<LineGridControl, bool>(nameof(UseFixedCellMetrics), false);

    public static readonly StyledProperty<double> FixedCellWidthProperty =
        AvaloniaProperty.Register<LineGridControl, double>(nameof(FixedCellWidth), 8d);

    public static readonly StyledProperty<double> FixedCellHeightProperty =
        AvaloniaProperty.Register<LineGridControl, double>(nameof(FixedCellHeight), 18d);

    private EditorController? _editor;
    private LineGridModel? _model;
    private FontFamily _fontFamily = new("Noto Sans Mono CJK JP, DejaVu Sans Mono, Noto Sans Mono, monospace");
    private double _cellWidth = 8;
    private double _cellHeight = 18;
    private double _overlayTextInset = 8;
    private bool _leftDown;
    private Rect? _popupRect;
    private int _popupFirstIndex;
    private int _popupVisibleCount;
    private int _popupLastSelectedIndex = -1;
    private Rect? _cmdlineRect;
    private Rect? _messagesRect;
    private readonly List<TabHitTarget> _tabHitTargets = new();
    private double _cmdlineScrollX;

    static LineGridControl()
    {
        AffectsRender<LineGridControl>(
            FontFamilyNameProperty,
            FontSizeProperty,
            LineHeightProperty,
            UseFixedCellMetricsProperty,
            FixedCellWidthProperty,
            FixedCellHeightProperty);
    }

    public LineGridControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public string FontFamilyName
    {
        get => GetValue(FontFamilyNameProperty);
        set => SetValue(FontFamilyNameProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public double LineHeight
    {
        get => GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public bool UseFixedCellMetrics
    {
        get => GetValue(UseFixedCellMetricsProperty);
        set => SetValue(UseFixedCellMetricsProperty, value);
    }

    public double FixedCellWidth
    {
        get => GetValue(FixedCellWidthProperty);
        set => SetValue(FixedCellWidthProperty, value);
    }

    public double FixedCellHeight
    {
        get => GetValue(FixedCellHeightProperty);
        set => SetValue(FixedCellHeightProperty, value);
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontFamilyNameProperty
            || change.Property == FontSizeProperty
            || change.Property == LineHeightProperty
            || change.Property == UseFixedCellMetricsProperty
            || change.Property == FixedCellWidthProperty
            || change.Property == FixedCellHeightProperty)
        {
            OnMetricsChanged();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_model is null) return;

        MeasureCell();
        ResetOverlayRects();

        RenderBackground(context);
        RenderNormalGrids(context);
        RenderFloatingGrids(context);
        RenderMessages(context);
        RenderShowMode(context);
        RenderShowCommand(context);
        RenderCmdline(context);
        RenderCmdlineBlock(context);
        RenderPopupMenu(context);
        RenderTabline(context);
        RenderCursor(context);
    }

    private void RenderBackground(DrawingContext context)
    {
        if (_model is null) return;
        context.FillRectangle(ToBrush(_model.DefaultBackground), new Rect(0, 0, Bounds.Width, Bounds.Height));
    }

    private void RenderNormalGrids(DrawingContext context)
    {
        if (_model is null) return;
        foreach (var grid in _model.VisibleGrids.Where(g => !g.Floating))
            RenderGrid(context, grid, drawShadow: false);
    }

    private void RenderFloatingGrids(DrawingContext context)
    {
        if (_model is null) return;
        foreach (var grid in _model.VisibleGrids.Where(g => g.Floating))
            RenderGrid(context, grid, drawShadow: true);
    }

    private void RenderPopupMenu(DrawingContext context)
    {
        if (_model?.PopupMenuState is not { HasItems: true } popup) return;

        var anchor = GetPopupAnchor(popup);
        var margin = 4d;
        var maxVisibleRows = Math.Max(1, (int)Math.Floor((Bounds.Height - (margin * 2)) / _cellHeight));
        var wordWidth = popup.Items.Select(item => MeasureOverlayText(item.Word).Width).DefaultIfEmpty(_cellWidth * 8).Max();
        var kindWidth = popup.Items.Select(item => MeasureOverlayText(item.Kind).Width).DefaultIfEmpty(0).Max();
        var menuWidth = popup.Items.Select(item => MeasureOverlayText(item.Menu).Width).DefaultIfEmpty(0).Max();
        var gapWidth = _cellWidth;
        var popupMinWidth = _cellWidth * 12;
        var popupMaxWidth = Math.Max(popupMinWidth, Math.Min(_cellWidth * 72, Bounds.Width * 0.9));
        var popupWidth = Math.Clamp(
            wordWidth + kindWidth + menuWidth + (gapWidth * 2) + 16,
            popupMinWidth,
            popupMaxWidth);
        var popupHeight = Math.Max(_cellHeight, Math.Min(maxVisibleRows, popup.Items.Count) * _cellHeight);

        var left = Math.Clamp(anchor.X, margin, Math.Max(margin, Bounds.Width - popupWidth - margin));
        var belowTop = anchor.Y + _cellHeight;
        var aboveTop = anchor.Y - popupHeight;
        var availableBelow = Math.Max(_cellHeight, Bounds.Height - belowTop - margin);
        var availableAbove = Math.Max(_cellHeight, anchor.Y - margin);
        var placeBelow = availableBelow >= popupHeight || availableBelow >= availableAbove;
        var maxHeight = Math.Max(_cellHeight, placeBelow ? availableBelow : availableAbove);
        popupHeight = Math.Min(popupHeight, maxHeight);
        _popupVisibleCount = Math.Max(1, Math.Min((int)Math.Floor(popupHeight / _cellHeight), popup.Items.Count));
        _popupFirstIndex = GetPopupFirstIndex(popup.Selected, popup.Items.Count, _popupVisibleCount);
        var top = placeBelow
            ? Math.Clamp(belowTop, margin, Math.Max(margin, Bounds.Height - popupHeight - margin))
            : Math.Clamp(aboveTop, margin, Math.Max(margin, Bounds.Height - popupHeight - margin));

        var rect = new Rect(left, top, popupWidth, popupHeight);
        _popupRect = rect;

        var pmenuStyle = GetUiStyle("Pmenu");
        var pmenuSelStyle = GetUiStyle("PmenuSel");
        var pmenuSbarStyle = GetUiStyle("PmenuSbar");
        var pmenuThumbStyle = GetUiStyle("PmenuThumb");
        var backgroundColor = BlendColor(ResolveBackground(pmenuStyle), "#ffffff", 0.06);
        var foregroundColor = ResolveForeground(pmenuStyle);
        var selectedBackground = BlendColor(ResolveBackground(pmenuSelStyle, ResolveBackground(pmenuStyle)), "#ffffff", 0.18);
        var selectedForegroundColor = ResolveForeground(pmenuSelStyle, foregroundColor);
        var borderBrush = ToBrush(pmenuStyle?.Special ?? pmenuStyle?.Foreground ?? "#ffffff24");
        var backgroundBrush = ToBrush(backgroundColor);
        var foregroundBrush = ToBrush(foregroundColor);
        var selectedBrush = ToBrush(selectedBackground);
        var selectedForeground = ToBrush(selectedForegroundColor);
        var scrollbarTrackBrush = ToBrush(BlendColor(ResolveBackground(pmenuSbarStyle, backgroundColor), "#ffffff", 0.08));
        var scrollbarThumbBrush = ToBrush(BlendColor(ResolveBackground(pmenuThumbStyle, ResolveBackground(pmenuSelStyle, foregroundColor)), "#ffffff", 0.18));

        DrawShadow(context, rect, 8, 0.20);
        context.FillRectangle(backgroundBrush, rect, 6);
        context.DrawRectangle(null, new Pen(borderBrush, 1), rect, 6);

        var visibleItems = popup.Items.Skip(_popupFirstIndex).Take(_popupVisibleCount).ToArray();
        var showScrollbar = _popupVisibleCount < popup.Items.Count;
        var scrollbarWidth = showScrollbar ? 10d : 0d;
        var contentRight = rect.Right - 8 - scrollbarWidth;
        var wordColumnWidth = Math.Max(_cellWidth * 4, contentRight - rect.X - 8 - kindWidth - menuWidth - (gapWidth * 2));
        for (var i = 0; i < visibleItems.Length; i++)
        {
            var item = visibleItems[i];
            var itemIndex = _popupFirstIndex + i;
            var itemRect = new Rect(rect.X, rect.Y + (i * _cellHeight), rect.Width, _cellHeight);
            if (itemIndex == popup.Selected)
                context.FillRectangle(selectedBrush, itemRect);

            var fg = itemIndex == popup.Selected ? selectedForeground : foregroundBrush;
            DrawOverlayText(context, item.Word, new Point(itemRect.X, itemRect.Y), fg, xInset: 8);
            DrawOverlayText(context, item.Kind, new Point(itemRect.X + 8 + wordColumnWidth + gapWidth, itemRect.Y), fg, opacity: 0.75, xInset: 0);
            DrawOverlayText(context, item.Menu, new Point(contentRight - menuWidth, itemRect.Y), fg, opacity: 0.75, xInset: 0);
        }

        if (showScrollbar)
        {
            var trackRect = new Rect(rect.Right - 10, rect.Y + 2, 8, rect.Height - 4);
            context.FillRectangle(scrollbarTrackBrush, trackRect, 4);

            var thumbHeight = Math.Max(_cellHeight, trackRect.Height * (_popupVisibleCount / (double)popup.Items.Count));
            var thumbRange = Math.Max(0, trackRect.Height - thumbHeight);
            var maxFirstIndex = Math.Max(1, popup.Items.Count - _popupVisibleCount);
            var thumbTop = trackRect.Y + (thumbRange * (_popupFirstIndex / (double)maxFirstIndex));
            var thumbRect = new Rect(trackRect.X + 1, thumbTop, Math.Max(4, trackRect.Width - 2), thumbHeight);
            context.FillRectangle(scrollbarThumbBrush, thumbRect, 999);
        }

        _popupLastSelectedIndex = popup.Selected;
    }

    private void RenderCmdline(DrawingContext context)
    {
        if (_model?.ActiveCmdline is not { } cmdline) return;
        var prefix = $"{cmdline.FirstChar}{cmdline.Prompt}{new string(' ', Math.Max(0, cmdline.Indent))}";
        var bodyChars = cmdline.Text.EnumerateRunes().Select(r => r.ToString()).ToList();
        var bodyCursor = Math.Max(0, Math.Min(bodyChars.Count, cmdline.Position));
        if (!string.IsNullOrEmpty(cmdline.SpecialChar))
        {
            if (cmdline.SpecialShift)
                bodyChars.Insert(bodyCursor, cmdline.SpecialChar);
            else if (bodyCursor < bodyChars.Count)
                bodyChars[bodyCursor] = cmdline.SpecialChar;
            else
                bodyChars.Add(cmdline.SpecialChar);
        }

        var fullChars = prefix.EnumerateRunes().Select(r => r.ToString()).Concat(bodyChars).ToList();
        var cursorIndex = Math.Max(0, Math.Min(fullChars.Count, prefix.EnumerateRunes().Count() + bodyCursor));
        var wrapPrompt = string.IsNullOrEmpty(cmdline.FirstChar) && !string.IsNullOrEmpty(cmdline.Prompt) && bodyChars.Count == 0;
        var text = string.Concat(fullChars);
        var rect = BottomOverlayRect(1, GetStatuslineOffsetRows());
        _cmdlineRect = rect;

        context.FillRectangle(ToBrush(_model.DefaultBackground), rect);
        var beforeCursor = cursorIndex <= 0
            ? string.Empty
            : string.Concat(fullChars.Take(cursorIndex));
        var cursorChar = cursorIndex >= 0 && cursorIndex < fullChars.Count ? fullChars[cursorIndex] : " ";
        var cursorWidth = Math.Max(_cellWidth, MeasureTextWidth(CursorCharForWidth(fullChars, cursorIndex)));
        var viewportWidth = Math.Max(1, rect.Width - (_overlayTextInset * 2));
        var cursorLeft = MeasureTextWidth(beforeCursor);
        var cursorRight = cursorLeft + cursorWidth;

        if (wrapPrompt)
        {
            _cmdlineScrollX = 0;
        }
        else
        {
            var viewportLeft = _cmdlineScrollX;
            var viewportRight = viewportLeft + viewportWidth;
            if (cursorRight > viewportRight)
                _cmdlineScrollX = Math.Max(0, cursorRight - viewportWidth);
            else if (cursorLeft < viewportLeft)
                _cmdlineScrollX = Math.Max(0, cursorLeft);
        }

        DrawOverlayTextClipped(context, text, rect, ToBrush(_model.DefaultForeground), -_cmdlineScrollX, _overlayTextInset);

        var cursorX = rect.X + _overlayTextInset + cursorLeft - _cmdlineScrollX;
        var cursorRect = new Rect(cursorX, rect.Y, cursorWidth, rect.Height);
        context.FillRectangle(ToBrush(_model.DefaultForeground), cursorRect);

        DrawOverlayText(context, cursorChar, new Point(cursorRect.X, cursorRect.Y), ToBrush(_model.DefaultBackground), xInset: 0);
    }

    private void RenderCmdlineBlock(DrawingContext context)
    {
        if (_model is null || _model.CmdlineBlock.Count == 0) return;
        var maxVisibleLines = Math.Max(2, (int)Math.Floor((Bounds.Height * 0.4) / _cellHeight));
        var lines = _model.CmdlineBlock.TakeLast(Math.Max(1, maxVisibleLines)).ToArray();
        var rect = BottomOverlayRect(lines.Length, GetStatuslineOffsetRows() + GetCmdlineRowCount());
        context.FillRectangle(ToBrush(_model.DefaultBackground), rect);
        for (var i = 0; i < lines.Length; i++)
            DrawOverlayText(context, lines[i], new Point(rect.X, rect.Y + (i * _cellHeight)), ToBrush(_model.DefaultForeground), xInset: _overlayTextInset);
    }

    private void RenderMessages(DrawingContext context)
    {
        if (_model is null) return;

        var activeEntries = _model.HistoryEntries.Count > 0 ? _model.HistoryEntries : _model.MessageEntries;
        if (_model.ActiveCmdline is null && activeEntries.Count == 0 && string.IsNullOrWhiteSpace(_model.ShowMode) && string.IsNullOrWhiteSpace(_model.ShowCommand) && string.IsNullOrWhiteSpace(_model.Ruler))
            return;

        if (_model.ActiveCmdline is null && activeEntries.Count == 0)
        {
            var statusRect = BottomOverlayRect(1, GetStatuslineOffsetRows());
            _messagesRect = statusRect;
            context.FillRectangle(ToBrush(_model.DefaultBackground), statusRect);

            var gap = _cellWidth;
            var paddedRect = new Rect(statusRect.X + _overlayTextInset, statusRect.Y, Math.Max(0, statusRect.Width - (_overlayTextInset * 2)), statusRect.Height);
            var rightWidth = Math.Max(0, MeasureOverlayText(string.IsNullOrEmpty(_model.Ruler) ? " " : _model.Ruler).Width);
            var leftAndMidWidth = Math.Max(0, paddedRect.Width - rightWidth - (string.IsNullOrWhiteSpace(_model.Ruler) ? 0 : gap));
            var leftWidth = Math.Max(0, (leftAndMidWidth - gap) / 2);
            var midWidth = Math.Max(0, leftAndMidWidth - leftWidth - gap);

            var leftRect = new Rect(paddedRect.X, paddedRect.Y, leftWidth, paddedRect.Height);
            var midRect = new Rect(leftRect.Right + gap, paddedRect.Y, midWidth, paddedRect.Height);
            var rightRect = new Rect(Math.Max(midRect.Right + gap, paddedRect.Right - rightWidth), paddedRect.Y, Math.Max(0, paddedRect.Right - Math.Max(midRect.Right + gap, paddedRect.Right - rightWidth)), paddedRect.Height);

            DrawOverlayTextInRect(context, _model.ShowMode, leftRect, ToBrush(_model.DefaultForeground), TextAlignment.Left);
            DrawOverlayTextInRect(context, _model.ShowCommand, midRect, ToBrush(_model.DefaultForeground), TextAlignment.Right);
            DrawOverlayTextInRect(context, _model.Ruler, rightRect, ToBrush(_model.DefaultForeground), TextAlignment.Right);
            return;
        }

        var visibleEntries = (_model.ActiveCmdline is not null ? activeEntries : activeEntries.TakeLast(Math.Min(activeEntries.Count, 8))).ToArray();
        if (visibleEntries.Length == 0) return;
        var wrappedLines = visibleEntries
            .SelectMany(entry => WrapMessageEntry(entry, Math.Max(1, Bounds.Width - (_overlayTextInset * 2))))
            .ToArray();
        if (wrappedLines.Length == 0) return;

        var rect = _model.MessageGridRow is int gridRow
            ? new Rect(0, GetEditorTopInset() + (gridRow * _cellHeight), Bounds.Width, wrappedLines.Length * _cellHeight)
            : BottomOverlayRect(wrappedLines.Length, GetStatuslineOffsetRows() + GetOverlayBottomInsetRows());
        _messagesRect = rect;

        for (var i = 0; i < wrappedLines.Length; i++)
        {
            var entry = wrappedLines[i];
            var lineRect = new Rect(rect.X, rect.Y + (i * _cellHeight), rect.Width, _cellHeight);
            context.FillRectangle(ToBrush(GetMessageBackground(entry)), lineRect);
            DrawMessageEntry(context, entry, lineRect);
        }
    }

    private void RenderShowMode(DrawingContext context)
    {
    }

    private void RenderShowCommand(DrawingContext context)
    {
    }

    private void DrawMessageEntry(DrawingContext context, MessageEntry entry, Rect rect)
    {
        var attentionHeader = "E325: ATTENTION";
        var x = rect.X + _overlayTextInset;
        var y = rect.Y + Math.Round((_cellHeight - MeasureOverlayText("A").Height) / 2);
        if (entry.Text.StartsWith(attentionHeader, StringComparison.Ordinal))
        {
            var codeBrush = Brushes.White;
            var codeBg = ToBrush("#8f2d2d");
            var codeText = MeasureOverlayText(attentionHeader);
            var codeRect = new Rect(x, rect.Y, codeText.Width + 4, rect.Height);
            context.FillRectangle(codeBg, codeRect);
            context.DrawText(new FormattedText(attentionHeader, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(_fontFamily), FontSize, codeBrush), new Point(x + 2, y));
            var rest = entry.Text.Substring(attentionHeader.Length);
            if (!string.IsNullOrEmpty(rest))
                DrawOverlayText(context, rest, new Point(codeRect.Right + 2, rect.Y), ToBrush(_model?.DefaultForeground ?? "#d4d4d4"), xInset: 0);
            return;
        }

        var chunks = entry.Chunks.Count > 0 ? entry.Chunks : [new MessageChunk(entry.Text, 0)];
        foreach (var chunk in chunks)
        {
            var (fg, _, style) = GetStyle(chunk.HlId);
            var text = new FormattedText(chunk.Text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, GetTypeface(style), FontSize, ToBrush(fg));
            context.DrawText(text, new Point(x, y));
            DrawCellDecorations(context, x, rect.Y, text.Width, fg, style);
            x += text.Width;
        }
    }

    private string GetMessageBackground(MessageEntry entry)
    {
        var baseBg = _model?.DefaultBackground ?? "#1e1e1e";
        return entry.Kind switch
        {
            "emsg" or "echoerr" => BlendColor(baseBg, "#7a1f1f", 0.28),
            "wmsg" => BlendColor(baseBg, "#7a5200", 0.22),
            "confirm" => baseBg,
            _ when entry.History => BlendColor(baseBg, "#ffffff", 0.10),
            _ => BlendColor(baseBg, "#ffffff", 0.08)
        };
    }

    private void RenderTabline(DrawingContext context)
    {
        if (_model is null || _model.TablineState.Tabs.Count == 0) return;
        _tabHitTargets.Clear();
        var topInset = GetEditorTopInset();
        var rect = new Rect(0, 0, Bounds.Width, Math.Max(1, topInset));
        context.FillRectangle(ToBrush(BlendColor(_model.DefaultBackground, "#ffffff", 0.06)), rect);
        context.DrawLine(new Pen(ToBrush("#ffffff1f"), 1), rect.BottomLeft, rect.BottomRight);

        var x = 6d;
        foreach (var tab in _model.TablineState.Tabs)
        {
            var fullLabel = string.IsNullOrWhiteSpace(tab.Label) ? $"tab {tab.Index}" : tab.Label;
            var badge = GetTabBadge(fullLabel);
            var label = GetTabDisplayLabel(fullLabel);
            var badgeText = MeasureOverlayText(badge);
            var labelText = MeasureOverlayText(label);
            var closeText = MeasureOverlayText("x");
            var width = Math.Min(Bounds.Width - x - 6, badgeText.Width + labelText.Width + closeText.Width + 34);
            if (width <= _cellWidth) break;

            var tabRect = new Rect(x, 1, width, Math.Max(1, rect.Height - 4));
            var background = tab.Current
                ? BlendColor(_model.DefaultBackground, "#ffffff", 0.20)
                : BlendColor(_model.DefaultBackground, "#ffffff", 0.08);
            context.FillRectangle(ToBrush(background), tabRect, 4);

            var badgeBg = ToBrush(BlendColor(background, "#ffffff", 0.10));
            var badgeRect = new Rect(tabRect.X + 8, tabRect.Y + 1, Math.Max(16, badgeText.Width + 8), Math.Max(1, tabRect.Height - 2));
            context.FillRectangle(badgeBg, badgeRect, 999);
            DrawOverlayText(context, badge, new Point(badgeRect.X + 4, tabRect.Y), ToBrush(_model.DefaultForeground), opacity: 0.85, xInset: 0);

            var labelPrefix = tab.Changed ? $"{label} +" : label;
            DrawOverlayText(context, labelPrefix, new Point(badgeRect.Right + 8, tabRect.Y), ToBrush(_model.DefaultForeground), xInset: 0);
            var closeRect = new Rect(tabRect.Right - closeText.Width - 10, tabRect.Y, closeText.Width + 8, tabRect.Height);
            DrawOverlayText(context, "x", new Point(closeRect.X + 2, tabRect.Y), ToBrush(_model.DefaultForeground), opacity: 0.7, xInset: 0);
            _tabHitTargets.Add(new TabHitTarget(tab.Index, false, tabRect));
            _tabHitTargets.Add(new TabHitTarget(tab.Index, true, closeRect));
            x += width + 4;
            if (x >= Bounds.Width - _cellWidth) break;
        }
    }

    private void RenderCursor(DrawingContext context)
    {
        if (_model is null || !_model.CursorVisible) return;
        if (!_model.Grids.TryGetValue(_model.CursorGrid, out var cursorGrid)) return;
        if (!cursorGrid.Visible || !InGrid(cursorGrid, _model.CursorRow, _model.CursorCol)) return;

        var cell = cursorGrid.Cells[_model.CursorRow][_model.CursorCol];
        var span = GetCellSpan(cursorGrid, _model.CursorRow, _model.CursorCol);
        var x = GetGridLeft(cursorGrid) + (_model.CursorCol * _cellWidth);
        var y = GetGridTop(cursorGrid) + (_model.CursorRow * _cellHeight);
        var width = Math.Min(Bounds.Width - x, span * _cellWidth);
        if (width <= 0 || y >= Bounds.Height) return;

        var (fg, bg, style) = GetStyle(cell.Hl);
        var cursorBrush = ToBrush(fg);
        var cursorRect = _model.CursorShape switch
        {
            "vertical" => new Rect(x, y, Math.Max(1, Math.Round((_cellWidth * _model.CurrentCursorModeState.CellPercentage) / 100d)), _cellHeight),
            "horizontal" => new Rect(x, y + (_cellHeight - Math.Max(1, Math.Round((_cellHeight * _model.CurrentCursorModeState.CellPercentage) / 100d))), width, Math.Max(1, Math.Round((_cellHeight * _model.CurrentCursorModeState.CellPercentage) / 100d))),
            _ => new Rect(x, y, width, _cellHeight),
        };

        context.FillRectangle(cursorBrush, cursorRect);
        if (_model.CursorShape == "block" && !string.IsNullOrEmpty(cell.Ch) && cell.Ch != " ")
            DrawCellForeground(context, x, y, cell.Ch, bg, new HighlightStyle(bg, fg, style?.Special ?? bg, false, style?.Bold == true, style?.Italic == true, style?.Underline == true, style?.Undercurl == true, style?.Strikethrough == true), width);
        else if (!string.IsNullOrEmpty(cell.Ch) && cell.Ch != " " && !cell.Continue)
            DrawCellForeground(context, x, y, cell.Ch, fg, style, width);
    }

    private void RenderGrid(DrawingContext context, GridState grid, bool drawShadow)
    {
        var left = GetGridLeft(grid);
        var top = GetGridTop(grid);
        var rect = new Rect(left, top, grid.Cols * _cellWidth, grid.Rows * _cellHeight);
        if (drawShadow)
        {
            DrawShadow(context, new Rect(rect.X + 6, rect.Y + 6, rect.Width, rect.Height), 0, 0.22);
            var sampleBackground = GetFloatingGridBackground(grid);
            context.FillRectangle(ToBrush(sampleBackground), rect);
            context.DrawRectangle(null, new Pen(ToBrush(BlendColor(sampleBackground, "#ffffff", 0.12)), 1), rect);
        }

        for (var row = 0; row < grid.Cells.Length; row++)
        {
            var y = top + (row * _cellHeight);
            if (y >= Bounds.Height || y + _cellHeight <= 0) continue;

            for (var col = 0; col < grid.Cells[row].Length; col++)
            {
                var x = left + (col * _cellWidth);
                if (x >= Bounds.Width) break;
                if (x + _cellWidth <= 0) continue;

                var cell = grid.Cells[row][col];
                var (_, bg, _) = GetStyle(cell.Hl);
                context.FillRectangle(ToBrush(bg), new Rect(x, y, _cellWidth, _cellHeight));
            }

            if (IsMainStatuslineRow(grid, row))
            {
                RenderStatuslineRow(context, grid, row, left, y);
                continue;
            }

            for (var col = 0; col < grid.Cells[row].Length; col++)
            {
                var cell = grid.Cells[row][col];
                if (cell.Continue)
                    continue;
                if (string.IsNullOrEmpty(cell.Ch) || cell.Ch == " ")
                    continue;

                var x = left + (col * _cellWidth);
                var width = GetCellSpan(grid, row, col) * _cellWidth;
                if (x >= Bounds.Width) break;
                if (x + width <= 0) continue;

                var (fg, _, style) = GetStyle(cell.Hl);
                DrawCellForeground(context, x, y, cell.Ch, fg, style, width);
            }
        }
    }

    private bool IsMainStatuslineRow(GridState grid, int row)
        => _model is not null
           && grid.Id == 1
           && row == grid.Rows - 1
           && grid.Rows == _model.Rows;

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

            var x = left + (startCol * _cellWidth);
            var width = Math.Max(1, ((endCol - startCol + 1) * _cellWidth));
            var (fg, _, style) = GetStyle(currentHl);
            DrawStatuslineRun(context, x, y, width, text, fg, style);
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

    private void DrawStatuslineRun(DrawingContext context, double x, double y, double width, string text, string foreground, HighlightStyle? style)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            GetTypeface(style),
            FontSize,
            ToBrush(foreground));

        var drawX = Math.Max(0, Math.Min(x, Math.Max(0, Bounds.Width - 1)));
        var drawY = y + Math.Round((_cellHeight - formatted.Height) / 2);
        var clipRect = new Rect(drawX, y, Math.Max(1, Math.Min(width + 4, Bounds.Width - drawX)), _cellHeight);
        using (context.PushClip(clipRect))
            context.DrawText(formatted, new Point(drawX, drawY));
        DrawCellDecorations(context, drawX, y, width, foreground, style);
    }

    private void DrawCellForeground(DrawingContext context, double x, double y, string text, string foreground, HighlightStyle? style, double width)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            GetTypeface(style),
            FontSize,
            ToBrush(foreground));

        var textX = width > (_cellWidth * 1.5) && formatted.Width < width
            ? x + Math.Floor((width - formatted.Width) / 2)
            : x;
        var clampedTextX = textX + formatted.Width > Bounds.Width - 1
            ? Math.Max(0, Bounds.Width - formatted.Width - 2)
            : textX;
        var textY = y + Math.Round((_cellHeight - formatted.Height) / 2);
        context.DrawText(formatted, new Point(clampedTextX, textY));
        DrawCellDecorations(context, x, y, width, foreground, style);
    }

    private void DrawCellDecorations(DrawingContext context, double x, double y, double width, string foreground, HighlightStyle? style)
    {
        if (style is null) return;
        var specialBrush = ToBrush(style.Special ?? foreground);

        if (style.Underline)
        {
            var underlineY = y + _cellHeight - 2;
            context.DrawLine(new Pen(specialBrush, 1), new Point(x, underlineY), new Point(x + width, underlineY));
        }

        if (style.Undercurl)
        {
            var baseY = y + _cellHeight - 2;
            var points = new List<Point>();
            var step = Math.Max(3, _cellWidth / 3);
            for (double dx = 0; dx <= width; dx += step)
            {
                var waveY = baseY + (((int)(dx / step) % 2 == 0) ? -1 : 1);
                points.Add(new Point(x + dx, waveY));
            }

            for (var i = 1; i < points.Count; i++)
                context.DrawLine(new Pen(specialBrush, 1), points[i - 1], points[i]);
        }

        if (style.Strikethrough)
        {
            var strikeY = y + (_cellHeight / 2);
            context.DrawLine(new Pen(specialBrush, 1), new Point(x, strikeY), new Point(x + width, strikeY));
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
        var pos = e.GetPosition(this);
        var tabHit = HitTestTabline(pos);
        if (tabHit is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            if (tabHit.CloseButton)
                await _editor.CloseTabAsync(tabHit.Index);
            else
                await _editor.SwitchTabAsync(tabHit.Index);
            return;
        }
        var (grid, row, col) = EventToGrid(pos);
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
        if (_model is null) return (1, 0, 0);

        if (_popupRect is { } popupRect && _model.PopupMenuState is { HasItems: true } popup && popupRect.Contains(pos))
        {
            var itemIndex = Math.Clamp(_popupFirstIndex + (int)((pos.Y - popupRect.Y) / _cellHeight), 0, popup.Items.Count - 1);
            return (popup.Grid, Math.Max(0, popup.Row + itemIndex), Math.Max(0, popup.Col));
        }

        if (_cmdlineRect is { } cmdlineRect && cmdlineRect.Contains(pos))
        {
            var col = Math.Max(0, Math.Min(_model.Cols - 1, (int)((pos.X - cmdlineRect.X) / _cellWidth)));
            return (-1, 0, col);
        }

        var grids = _model.VisibleGrids
            .OrderByDescending(g => g.ZIndex)
            .ThenByDescending(g => g.Floating)
            .ThenByDescending(g => g.Id);

        foreach (var grid in grids)
        {
            var left = GetGridLeft(grid);
            var top = GetGridTop(grid);
            var right = left + (grid.Cols * _cellWidth);
            var bottom = top + (grid.Rows * _cellHeight);
            if (pos.X < left || pos.X >= right || pos.Y < top || pos.Y >= bottom)
                continue;

            var row = Math.Max(0, Math.Min(grid.Rows - 1, (int)((pos.Y - top) / _cellHeight)));
            var col = Math.Max(0, Math.Min(grid.Cols - 1, (int)((pos.X - left) / _cellWidth)));
            return (grid.Id, row, col);
        }

        return (1, 0, 0);
    }

    private async Task ResizeNvimAsync()
    {
        if (_editor is null) return;
        MeasureCell();
        var cols = Math.Max(2, (int)(Bounds.Width / _cellWidth));
        var rows = Math.Max(2, (int)(Bounds.Height / _cellHeight) - Math.Max(0, _model?.EditorTopOffset ?? 0));
        await _editor.ResizeAsync(cols, rows);
    }

    private void MeasureCell()
    {
        _fontFamily = new FontFamily(FontFamilyName);
        if (UseFixedCellMetrics)
        {
            _cellWidth = Math.Max(1, FixedCellWidth);
            _cellHeight = Math.Max(1, FixedCellHeight);
            return;
        }

        var probeTypeface = new Typeface(_fontFamily);
        var narrowProbe = new FormattedText("0", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, probeTypeface, FontSize, Brushes.White);
        var asciiProbe = new FormattedText("abcdefghijklmnopqrstuvwxyz", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, probeTypeface, FontSize, Brushes.White);
        var wideProbe = new FormattedText("漢", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, probeTypeface, FontSize, Brushes.White);
        var narrowWidth = Math.Max(narrowProbe.Width, asciiProbe.Width / 26d);
        var wideWidth = wideProbe.Width / 2d;
        _cellWidth = Math.Max(1, Math.Round(Math.Max(narrowWidth, wideWidth)));
        _cellHeight = Math.Max(1, Math.Ceiling(FontSize * LineHeight));
    }

    private void OnMetricsChanged()
    {
        MeasureCell();
        InvalidateVisual();
        if (_editor is not null)
            _ = Dispatcher.UIThread.InvokeAsync(ResizeNvimAsync);
    }

    private double GetGridTop(GridState grid) => GetEditorTopInset() + (grid.Row * _cellHeight);
    private double GetGridLeft(GridState grid) => grid.Col * _cellWidth;

    private Rect BottomOverlayRect(int heightInRows, int offsetRows)
    {
        var height = Math.Max(1, heightInRows * _cellHeight);
        var y = Math.Max(GetEditorTopInset(), Bounds.Height - ((offsetRows + heightInRows) * _cellHeight));
        return new Rect(0, y, Bounds.Width, height);
    }

    private double GetEditorTopInset()
        => ((_model?.EditorTopOffset ?? 0) * _cellHeight) + (((_model?.TablineState.Tabs.Count ?? 0) > 0) ? 2 : 0);

    private int GetCmdlineRowCount() => _model?.ActiveCmdline is not null ? 1 : 0;

    private int GetCmdlineBlockRowCount() => _model?.CmdlineBlock.Count ?? 0;

    private int GetOverlayBottomInsetRows() => GetCmdlineRowCount() + GetCmdlineBlockRowCount();

    private int GetStatuslineOffsetRows() => 1;

    private Point GetPopupAnchor(PopupMenuState popup)
    {
        if (popup.Grid == -1 && _cmdlineRect is { } cmdlineRect)
            return new Point(cmdlineRect.X + (popup.Col * _cellWidth), cmdlineRect.Y + (popup.Row * _cellHeight));

        if (_model is null || !_model.Grids.TryGetValue(popup.Grid, out var grid))
            return new Point(0, 0);

        return new Point(GetGridLeft(grid) + (popup.Col * _cellWidth), GetGridTop(grid) + (popup.Row * _cellHeight));
    }

    private int GetPopupFirstIndex(int selected, int totalCount, int visibleCount)
    {
        if (visibleCount >= totalCount) return 0;
        var clampedSelected = Math.Clamp(selected, 0, totalCount - 1);
        var current = Math.Clamp(_popupFirstIndex, 0, Math.Max(0, totalCount - visibleCount));
        if (_popupLastSelectedIndex < 0)
            return Math.Clamp(clampedSelected - (visibleCount / 2), 0, totalCount - visibleCount);
        if (clampedSelected < current)
            return clampedSelected;
        if (clampedSelected >= current + visibleCount)
            return Math.Clamp(clampedSelected - visibleCount + 1, 0, totalCount - visibleCount);
        return current;
    }

    private int GetCellSpan(GridState grid, int row, int col)
    {
        var span = 1;
        for (var next = col + 1; next < grid.Cols && grid.Cells[row][next].Continue; next++)
            span++;
        return span;
    }

    private bool InGrid(GridState grid, int row, int col) => row >= 0 && row < grid.Rows && col >= 0 && col < grid.Cols;

    private Typeface GetTypeface(HighlightStyle? style)
    {
        var fontStyle = style?.Italic == true ? FontStyle.Italic : FontStyle.Normal;
        var fontWeight = style?.Bold == true ? FontWeight.Bold : FontWeight.Normal;
        return new Typeface(_fontFamily, fontStyle, fontWeight);
    }

    private (string fg, string bg, HighlightStyle? style) GetStyle(int hlId)
    {
        HighlightStyle? style = null;
        _model?.Highlights.TryGetValue(hlId, out style);

        var fg = !string.IsNullOrWhiteSpace(style?.Foreground)
            ? style.Foreground!
            : _model?.DefaultForeground ?? "#d4d4d4";

        var bg = !string.IsNullOrWhiteSpace(style?.Background)
            ? style.Background!
            : _model?.DefaultBackground ?? "#1e1e1e";

        if (style?.Reverse == true)
            (fg, bg) = (bg, fg);

        return (fg, bg, style);
    }

    private HighlightStyle? GetUiStyle(string groupName)
    {
        if (_model is null) return null;
        foreach (var highlight in _model.Highlights.Values)
        {
            _ = highlight;
        }
        return groupName switch
        {
            "PmenuSel" => new HighlightStyle(_model.DefaultForeground, "#3b3b3b", "#6b6b6b", false, false, false, false, false, false),
            "Pmenu" => new HighlightStyle(_model.DefaultForeground, "#202020", "#555555", false, false, false, false, false, false),
            "PmenuSbar" => new HighlightStyle(null, "#2a2a2a", null, false, false, false, false, false, false),
            "PmenuThumb" => new HighlightStyle(null, "#6a6a6a", null, false, false, false, false, false, false),
            _ => null
        };
    }

    private string ResolveForeground(HighlightStyle? style, string? fallback = null)
        => !string.IsNullOrWhiteSpace(style?.Foreground) ? style.Foreground! : fallback ?? _model?.DefaultForeground ?? "#d4d4d4";

    private string ResolveBackground(HighlightStyle? style, string? fallback = null)
        => !string.IsNullOrWhiteSpace(style?.Background) ? style.Background! : fallback ?? _model?.DefaultBackground ?? "#1e1e1e";

    private FormattedText MeasureOverlayText(string text)
        => new(
            string.IsNullOrEmpty(text) ? " " : text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(_fontFamily),
            FontSize,
            Brushes.White);

    private void DrawOverlayText(DrawingContext context, string text, Point point, IBrush brush, double opacity = 1, double xInset = 4)
    {
        var formatted = new FormattedText(
            string.IsNullOrEmpty(text) ? " " : text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(_fontFamily),
            FontSize,
            brush);
        var y = point.Y + Math.Round((_cellHeight - formatted.Height) / 2);
        if (opacity >= 1)
        {
            context.DrawText(formatted, new Point(point.X + xInset, y));
            return;
        }

        using (context.PushOpacity(opacity))
            context.DrawText(formatted, new Point(point.X + xInset, y));
    }

    private void DrawOverlayTextClipped(DrawingContext context, string text, Rect rect, IBrush brush, double xOffset = 0, double xInset = 0, double opacity = 1)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || string.IsNullOrEmpty(text))
            return;

        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(_fontFamily),
            FontSize,
            brush);
        var y = rect.Y + Math.Round((_cellHeight - formatted.Height) / 2);

        using (context.PushClip(rect))
        {
            if (opacity >= 1)
                context.DrawText(formatted, new Point(rect.X + xInset + xOffset, y));
            else
            {
                using (context.PushOpacity(opacity))
                    context.DrawText(formatted, new Point(rect.X + xInset + xOffset, y));
            }
        }
    }

    private void DrawOverlayTextInRect(DrawingContext context, string text, Rect rect, IBrush brush, TextAlignment alignment, double opacity = 1)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || string.IsNullOrEmpty(text))
            return;

        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(_fontFamily),
            FontSize,
            brush);

        var x = alignment == TextAlignment.Right
            ? Math.Max(rect.X, rect.Right - formatted.Width)
            : rect.X;
        var y = rect.Y + Math.Round((_cellHeight - formatted.Height) / 2);

        using (context.PushClip(rect))
        {
            if (opacity >= 1)
                context.DrawText(formatted, new Point(x, y));
            else
            {
                using (context.PushOpacity(opacity))
                    context.DrawText(formatted, new Point(x, y));
            }
        }
    }

    private double MeasureTextWidth(string text)
    {
        var formatted = new FormattedText(
            string.IsNullOrEmpty(text) ? " " : text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(_fontFamily),
            FontSize,
            Brushes.White);
        return formatted.Width;
    }

    private IReadOnlyList<MessageEntry> WrapMessageEntry(MessageEntry entry, double maxWidth)
    {
        if (maxWidth <= 0)
            return [entry];

        var lines = new List<MessageEntry>();
        var currentChunks = new List<MessageChunk>();
        var currentWidth = 0d;

        foreach (var chunk in (entry.Chunks.Count > 0 ? entry.Chunks : [new MessageChunk(entry.Text, 0)]))
        {
            var chars = chunk.Text.EnumerateRunes().Select(r => r.ToString()).ToArray();
            var currentText = string.Empty;

            foreach (var ch in chars)
            {
                if (ch == "\n")
                {
                    if (currentText.Length > 0)
                    {
                        currentChunks.Add(new MessageChunk(currentText, chunk.HlId));
                        currentText = string.Empty;
                    }
                    lines.Add(new MessageEntry(string.Concat(currentChunks.Select(c => c.Text)), entry.Kind, currentChunks.ToArray(), entry.History));
                    currentChunks = new List<MessageChunk>();
                    currentWidth = 0;
                    continue;
                }

                var chWidth = MeasureTextWidth(ch);
                if (currentWidth > 0 && currentWidth + chWidth > maxWidth)
                {
                    if (currentText.Length > 0)
                    {
                        currentChunks.Add(new MessageChunk(currentText, chunk.HlId));
                        currentText = string.Empty;
                    }
                    lines.Add(new MessageEntry(string.Concat(currentChunks.Select(c => c.Text)), entry.Kind, currentChunks.ToArray(), entry.History));
                    currentChunks = new List<MessageChunk>();
                    currentWidth = 0;
                }

                currentText += ch;
                currentWidth += chWidth;
            }

            if (currentText.Length > 0)
                currentChunks.Add(new MessageChunk(currentText, chunk.HlId));
        }

        if (currentChunks.Count > 0)
            lines.Add(new MessageEntry(string.Concat(currentChunks.Select(c => c.Text)), entry.Kind, currentChunks.ToArray(), entry.History));

        return lines.Count > 0 ? lines : [entry];
    }

    private void DrawShadow(DrawingContext context, Rect rect, double spread, double opacity)
    {
        var shadowRect = rect.Inflate(spread);
        using (context.PushOpacity(opacity))
            context.FillRectangle(Brushes.Black, shadowRect, 6);
    }

    private string GetFloatingGridBackground(GridState grid)
    {
        for (var row = 0; row < grid.Cells.Length; row++)
        {
            for (var col = 0; col < grid.Cells[row].Length; col++)
            {
                var cell = grid.Cells[row][col];
                var (_, bg, _) = GetStyle(cell.Hl);
                if (!string.Equals(bg, _model?.DefaultBackground, StringComparison.OrdinalIgnoreCase))
                    return bg;
            }
        }

        return _model?.DefaultBackground ?? "#1e1e1e";
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

    private static string BlendColor(string baseColor, string overlayColor, double overlayRatio)
    {
        overlayRatio = Math.Clamp(overlayRatio, 0, 1);
        var baseParsed = Color.Parse(baseColor);
        var overlayParsed = Color.Parse(overlayColor);
        byte Mix(byte a, byte b) => (byte)Math.Clamp(Math.Round((a * (1 - overlayRatio)) + (b * overlayRatio)), 0, 255);
        return $"#{Mix(baseParsed.R, overlayParsed.R):x2}{Mix(baseParsed.G, overlayParsed.G):x2}{Mix(baseParsed.B, overlayParsed.B):x2}";
    }

    private static string CursorCharForWidth(IReadOnlyList<string> fullChars, int cursorIndex)
        => cursorIndex >= 0 && cursorIndex < fullChars.Count ? fullChars[cursorIndex] : " ";

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

    private void ResetOverlayRects()
    {
        _popupRect = null;
        _cmdlineRect = null;
        _messagesRect = null;
        _tabHitTargets.Clear();
        _popupFirstIndex = 0;
        _popupVisibleCount = 0;
        if (_model?.PopupMenuState is null)
            _popupLastSelectedIndex = -1;
    }

    private TabHitTarget? HitTestTabline(Point pos)
    {
        if (_model is null || _model.TablineState.Tabs.Count == 0)
            return null;
        if (pos.Y < 0 || pos.Y > _cellHeight)
            return null;
        return _tabHitTargets
            .OrderByDescending(t => t.CloseButton)
            .FirstOrDefault(t => t.Rect.Contains(pos));
    }

    private static IBrush ToBrush(string color) => Brush.Parse(color);

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

    private sealed record TabHitTarget(int Index, bool CloseButton, Rect Rect);
}
