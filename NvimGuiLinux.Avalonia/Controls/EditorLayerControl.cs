using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using NvimGuiCommon.Diagnostics;
using NvimGuiCommon.Editor;
using System.Globalization;
using System.Text;

namespace NvimGuiLinux.Avalonia.Controls;

public abstract class EditorLayerControl : Control
{
    private const string DefaultEditorFontFamily = "Sarasa Mono J, Noto Sans Mono CJK JP, Noto Sans Mono, BIZ UDGothic, MS Gothic, IPAGothic, VL Gothic, DejaVu Sans Mono, monospace";

    public static readonly StyledProperty<string> FontFamilyNameProperty =
        AvaloniaProperty.Register<EditorLayerControl, string>(nameof(FontFamilyName), DefaultEditorFontFamily);

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<EditorLayerControl, double>(nameof(FontSize), 14d);

    public static readonly StyledProperty<double> LineHeightProperty =
        AvaloniaProperty.Register<EditorLayerControl, double>(nameof(LineHeight), 1.1d);

    public static readonly StyledProperty<bool> UseFixedCellMetricsProperty =
        AvaloniaProperty.Register<EditorLayerControl, bool>(nameof(UseFixedCellMetrics), false);

    public static readonly StyledProperty<double> FixedCellWidthProperty =
        AvaloniaProperty.Register<EditorLayerControl, double>(nameof(FixedCellWidth), 8d);

    public static readonly StyledProperty<double> FixedCellHeightProperty =
        AvaloniaProperty.Register<EditorLayerControl, double>(nameof(FixedCellHeight), 18d);

    private Action? _changedHandler;
    private static readonly Dictionary<string, EditorRenderMetrics> MetricsCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Typeface> TypefaceMatchCache = new(StringComparer.Ordinal);
    private EditorRenderMetrics? _metrics;

    protected EditorLayerControl()
    {
        Focusable = false;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    static EditorLayerControl()
    {
        AffectsRender<EditorLayerControl>(
            FontFamilyNameProperty,
            FontSizeProperty,
            LineHeightProperty,
            UseFixedCellMetricsProperty,
            FixedCellWidthProperty,
            FixedCellHeightProperty);
    }

    protected LineGridModel? Model { get; private set; }
    protected FontFamily FontFamilyRef => Metrics.FontFamily;
    protected double CellWidth => Metrics.CellWidth;
    protected double CellHeight => Metrics.CellHeight;
    protected double OverlayTextInset { get; } = 8;
    private EditorRenderMetrics Metrics => _metrics ??= ComputeMetrics();

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

    public virtual void Bind(LineGridModel model)
    {
        if (ReferenceEquals(Model, model))
            return;

        if (Model is not null && _changedHandler is not null)
            Model.Changed -= _changedHandler;

        Model = model;
        _changedHandler = () => Dispatcher.UIThread.Post(InvalidateVisual);
        Model.Changed += _changedHandler;
        MeasureCell();
        InvalidateVisual();
    }

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

    protected virtual void OnMetricsChanged()
    {
        _metrics = ComputeMetrics();
        InvalidateVisual();
    }

    protected void MeasureCell()
    {
        _metrics = ComputeMetrics();
    }

    protected void RenderGrid(DrawingContext context, GridState grid, bool drawShadow)
    {
        var rect = GetGridRect(grid);
        var left = rect.X;
        var top = rect.Y;
        if (drawShadow)
        {
            DrawShadow(context, new Rect(rect.X + 6, rect.Y + 6, rect.Width, rect.Height), 0, 0.22);
            var sampleBackground = GetFloatingGridBackground(grid);
            context.FillRectangle(ToBrush(sampleBackground), rect);
            context.DrawRectangle(null, new Pen(ToBrush(BlendColor(sampleBackground, "#ffffff", 0.12)), 1), rect);
        }

        for (var row = 0; row < grid.Cells.Length; row++)
        {
            if (IsSpecialGridRow(grid, row))
            {
                var specialY = top + (row * CellHeight);
                if (specialY >= Bounds.Height || specialY + CellHeight <= 0)
                    continue;

                RenderSpecialGridRow(context, grid, row, left, specialY);
                continue;
            }

            var y = top + (row * CellHeight);
            if (y >= Bounds.Height || y + CellHeight <= 0)
                continue;

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
    }

    protected virtual bool IsSpecialGridRow(GridState grid, int row) => false;

    protected virtual void RenderSpecialGridRow(DrawingContext context, GridState grid, int row, double left, double y)
    {
    }

    protected void RenderCursorOnGrid(DrawingContext context, GridState grid)
    {
        if (Model is null || !Model.CursorVisible)
            return;
        if (!InGrid(grid, Model.CursorRow, Model.CursorCol))
            return;

        var cell = grid.Cells[Model.CursorRow][Model.CursorCol];
        var x = GetGridLeft(grid) + (Model.CursorCol * CellWidth);
        var y = GetGridTop(grid) + (Model.CursorRow * CellHeight);
        var span = GetCellSpan(grid, Model.CursorRow, Model.CursorCol);
        var width = Math.Min(Bounds.Width - x, span * CellWidth);
        if (width <= 0 || y >= Bounds.Height)
            return;

        var (fg, bg, style) = GetStyle(cell.Hl);
        var cursorBrush = ToBrush(fg);
        var cursorRect = Model.CursorShape switch
        {
            "vertical" => new Rect(x, y, Math.Max(1, Math.Round((CellWidth * Model.CurrentCursorModeState.CellPercentage) / 100d)), CellHeight),
            "horizontal" => new Rect(x, y + (CellHeight - Math.Max(1, Math.Round((CellHeight * Model.CurrentCursorModeState.CellPercentage) / 100d))), width, Math.Max(1, Math.Round((CellHeight * Model.CurrentCursorModeState.CellPercentage) / 100d))),
            _ => new Rect(x, y, width, CellHeight),
        };

        context.FillRectangle(cursorBrush, cursorRect);
        if (Model.CursorShape == "block" && !string.IsNullOrEmpty(cell.Ch) && cell.Ch != " ")
        {
            DrawCellForeground(
                context,
                x,
                y,
                cell.Ch,
                bg,
                new HighlightStyle(bg, fg, style?.Special ?? bg, false, style?.Bold == true, style?.Italic == true, style?.Underline == true, style?.Undercurl == true, style?.Strikethrough == true)
                {
                    Blend = style?.Blend ?? 0
                },
                width);
        }
        else if (!string.IsNullOrEmpty(cell.Ch) && cell.Ch != " " && !cell.Continue)
        {
            DrawCellForeground(context, x, y, cell.Ch, fg, style, width);
        }
    }

    protected double GetGridTop(GridState grid)
        => CreateLayoutSnapshot().GetGridTop(grid);

    protected double GetGridLeft(GridState grid)
        => CreateLayoutSnapshot().GetGridLeft(grid);

    protected Rect GetGridRect(GridState grid)
        => CreateLayoutSnapshot().GetGridRect(grid);

    protected Rect BottomOverlayRect(int heightInRows, int offsetRows)
        => CreateLayoutSnapshot().BottomOverlayRect(heightInRows, offsetRows);

    protected Rect? GetCmdlineRect()
    {
        if (Model?.ActiveCmdline is null)
            return null;
        return BottomOverlayRect(1, 0);
    }

    protected int GetStatuslineOffsetRows() => 0;

    protected int GetCmdlineRowCount() => Model?.ActiveCmdline is not null ? 1 : 0;

    protected IReadOnlyList<string> GetVisibleCmdlineBlockLines()
        => Model?.CmdlineBlock.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray() ?? Array.Empty<string>();

    protected int GetCmdlineBlockRowCount() => GetVisibleCmdlineBlockLines().Count;

    protected int GetOverlayBottomInsetRows() => GetCmdlineRowCount() + GetCmdlineBlockRowCount();

    protected double GetEditorTopInset()
        => ((Model?.EditorTopOffset ?? 0) * CellHeight) + (((Model?.TablineState.Tabs.Count ?? 0) > 0) ? 2 : 0);

    protected double GetBottomOverlayRowTop()
    {
        var fallback = Math.Max(GetEditorTopInset(), Bounds.Height - CellHeight);
        if (Model is null)
            return fallback;

        var primaryGrid = Model.VisibleGrids
            .Where(g => !g.Floating && g.Visible)
            .OrderByDescending(g => g.Id == 1)
            .ThenByDescending(g => g.EffectiveZIndex)
            .ThenByDescending(g => g.Id)
            .FirstOrDefault();

        if (primaryGrid is null)
            return fallback;

        var row = primaryGrid.RenderRow;
        if (primaryGrid.Floating && (string.Equals(primaryGrid.FloatAnchor, "SW", StringComparison.OrdinalIgnoreCase) || string.Equals(primaryGrid.FloatAnchor, "SE", StringComparison.OrdinalIgnoreCase)))
            row -= Math.Max(0, primaryGrid.Rows - 1);
        var candidate = GetEditorTopInset() + ((row + primaryGrid.Rows) * CellHeight);
        return Math.Clamp(candidate, GetEditorTopInset(), fallback);
    }

    protected EditorLayoutSnapshot CreateLayoutSnapshot()
        => new(
            CellWidth,
            CellHeight,
            GetEditorTopInset(),
            GetBottomOverlayRowTop(),
            Bounds.Width,
            Bounds.Height);

    protected PopupMenuLayout CalculatePopupMenuLayout(PopupMenuState popup, Rect? cmdlineRect, int previousFirstIndex, int previousLastSelectedIndex)
    {
        var anchor = GetPopupAnchor(popup, cmdlineRect);
        var margin = 4d;
        var maxVisibleRows = Math.Max(1, (int)Math.Floor((Bounds.Height - (margin * 2)) / Math.Max(1, CellHeight)));
        var wordWidth = popup.Items.Select(item => MeasureOverlayText(item.Word).Width).DefaultIfEmpty(CellWidth * 8).Max();
        var kindWidth = popup.Items.Select(item => MeasureOverlayText(item.Kind).Width).DefaultIfEmpty(0).Max();
        var extraWidth = popup.Items.Select(item => MeasureOverlayText(string.IsNullOrWhiteSpace(item.Menu) ? item.Info : item.Menu).Width).DefaultIfEmpty(0).Max();
        var gapWidth = CellWidth;
        var popupMinWidth = CellWidth * 12;
        var popupMaxWidth = Math.Max(popupMinWidth, Math.Min(CellWidth * 72, Bounds.Width * 0.9));
        var popupWidth = Math.Clamp(
            wordWidth + kindWidth + extraWidth + (gapWidth * 2) + 16,
            popupMinWidth,
            popupMaxWidth);
        var popupHeight = Math.Max(CellHeight, Math.Min(maxVisibleRows, popup.Items.Count) * CellHeight);

        var left = Math.Clamp(anchor.X, margin, Math.Max(margin, Bounds.Width - popupWidth - margin));
        var belowTop = anchor.Y + CellHeight;
        var aboveTop = anchor.Y - popupHeight;
        var availableBelow = Math.Max(CellHeight, Bounds.Height - belowTop - margin);
        var availableAbove = Math.Max(CellHeight, anchor.Y - margin);
        var placeBelow = availableBelow >= popupHeight || availableBelow >= availableAbove;
        var maxHeight = Math.Max(CellHeight, placeBelow ? availableBelow : availableAbove);
        popupHeight = Math.Min(popupHeight, maxHeight);

        var visibleCount = Math.Max(1, Math.Min((int)Math.Floor(popupHeight / CellHeight), popup.Items.Count));
        var firstIndex = GetPopupFirstIndex(popup.Selected, popup.Items.Count, visibleCount, previousFirstIndex, previousLastSelectedIndex);
        var top = placeBelow
            ? Math.Clamp(belowTop, margin, Math.Max(margin, Bounds.Height - popupHeight - margin))
            : Math.Clamp(aboveTop, margin, Math.Max(margin, Bounds.Height - popupHeight - margin));

        var scrollbarWidth = visibleCount < popup.Items.Count ? 10d : 0d;
        var availableContentWidth = Math.Max(CellWidth * 4, popupWidth - 16 - scrollbarWidth - kindWidth - extraWidth - (gapWidth * 2));

        return new PopupMenuLayout(
            new Rect(left, top, popupWidth, popupHeight),
            firstIndex,
            visibleCount,
            availableContentWidth,
            kindWidth,
            extraWidth,
            gapWidth,
            scrollbarWidth);
    }

    protected void DrawPopupMenu(DrawingContext context, PopupMenuState popup, PopupMenuLayout layout)
    {
        var rect = layout.Rect;
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
        var foregroundBrush = ToBrush(selectedForegroundColor == backgroundColor ? ResolveForeground(pmenuStyle) : foregroundColor);
        var selectedBrush = ToBrush(selectedBackground);
        var selectedForeground = ToBrush(selectedForegroundColor);
        var scrollbarTrackBrush = ToBrush(BlendColor(ResolveBackground(pmenuSbarStyle, backgroundColor), "#ffffff", 0.08));
        var scrollbarThumbBrush = ToBrush(BlendColor(ResolveBackground(pmenuThumbStyle, ResolveBackground(pmenuSelStyle, foregroundColor)), "#ffffff", 0.18));

        DrawShadow(context, rect, 8, 0.20);
        context.FillRectangle(backgroundBrush, rect, 6);
        context.DrawRectangle(null, new Pen(borderBrush, 1), rect, 6);

        var visibleItems = popup.Items.Skip(layout.FirstIndex).Take(layout.VisibleCount).ToArray();
        var contentRight = rect.Right - 8 - layout.ScrollbarWidth;
        for (var i = 0; i < visibleItems.Length; i++)
        {
            var item = visibleItems[i];
            var itemIndex = layout.FirstIndex + i;
            var itemRect = new Rect(rect.X, rect.Y + (i * CellHeight), rect.Width, CellHeight);
            if (itemIndex == popup.Selected)
                context.FillRectangle(selectedBrush, itemRect);

            var fg = itemIndex == popup.Selected ? selectedForeground : foregroundBrush;
            var extraText = string.IsNullOrWhiteSpace(item.Menu) ? item.Info : item.Menu;

            var wordRect = new Rect(itemRect.X + 8, itemRect.Y, layout.WordColumnWidth, itemRect.Height);
            var kindRect = new Rect(wordRect.Right + layout.GapWidth, itemRect.Y, layout.KindWidth, itemRect.Height);
            var extraRect = new Rect(kindRect.Right + layout.GapWidth, itemRect.Y, Math.Max(0, contentRight - (kindRect.Right + layout.GapWidth)), itemRect.Height);

            DrawOverlayTextInRect(context, item.Word, wordRect, fg, TextAlignment.Left);
            DrawOverlayTextInRect(context, item.Kind, kindRect, fg, TextAlignment.Left, 0.75);
            DrawOverlayTextInRect(context, extraText, extraRect, fg, TextAlignment.Right, 0.75);
        }

        if (layout.ScrollbarWidth > 0)
        {
            var trackRect = new Rect(rect.Right - layout.ScrollbarWidth, rect.Y + 2, layout.ScrollbarWidth - 2, rect.Height - 4);
            context.FillRectangle(scrollbarTrackBrush, trackRect, 4);

            var thumbHeight = Math.Max(CellHeight, trackRect.Height * (layout.VisibleCount / (double)popup.Items.Count));
            var thumbRange = Math.Max(0, trackRect.Height - thumbHeight);
            var maxFirstIndex = Math.Max(1, popup.Items.Count - layout.VisibleCount);
            var thumbTop = trackRect.Y + (thumbRange * (layout.FirstIndex / (double)maxFirstIndex));
            var thumbRect = new Rect(trackRect.X + 1, thumbTop, Math.Max(4, trackRect.Width - 2), thumbHeight);
            context.FillRectangle(scrollbarThumbBrush, thumbRect, 999);
        }
    }

    protected Point GetPopupAnchor(PopupMenuState popup, Rect? cmdlineRect)
    {
        if (popup.Grid == -1 && cmdlineRect is { } visibleCmdlineRect)
            return new Point(visibleCmdlineRect.X + (popup.Col * CellWidth), visibleCmdlineRect.Y + (popup.Row * CellHeight));

        if (Model is null || !Model.Grids.TryGetValue(popup.Grid, out var grid))
            return new Point(0, 0);

        return new Point(GetGridLeft(grid) + (popup.Col * CellWidth), GetGridTop(grid) + (popup.Row * CellHeight));
    }

    protected int GetCellSpan(GridState grid, int row, int col)
    {
        var span = 1;
        for (var next = col + 1; next < grid.Cols && grid.Cells[row][next].Continue; next++)
            span++;
        return span;
    }

    protected bool InGrid(GridState grid, int row, int col) => row >= 0 && row < grid.Rows && col >= 0 && col < grid.Cols;

    protected Typeface GetTypeface(HighlightStyle? style)
    {
        var fontStyle = style?.Italic == true ? FontStyle.Italic : FontStyle.Normal;
        var fontWeight = style?.Bold == true ? FontWeight.Bold : FontWeight.Normal;
        return new Typeface(FontFamilyRef, fontStyle, fontWeight);
    }

    protected (string fg, string bg, HighlightStyle? style) GetStyle(int hlId)
    {
        HighlightStyle? style = null;
        Model?.Highlights.TryGetValue(hlId, out style);

        var fg = !string.IsNullOrWhiteSpace(style?.Foreground)
            ? style.Foreground!
            : Model?.DefaultForeground ?? "#d4d4d4";

        var bg = !string.IsNullOrWhiteSpace(style?.Background)
            ? style.Background!
            : Model?.DefaultBackground ?? "#1e1e1e";

        if (style?.Reverse == true)
            (fg, bg) = (bg, fg);

        bg = ApplyBlend(bg, style, Model?.DefaultBackground ?? "#1e1e1e");
        if (string.Equals(fg, bg, StringComparison.OrdinalIgnoreCase))
            fg = ReadableForegroundForBackground(bg);

        return (fg, bg, style);
    }

    protected HighlightStyle? GetUiStyle(string groupName)
    {
        if (Model is null)
            return null;

        return groupName switch
        {
            "PmenuSel" => new HighlightStyle(Model.DefaultForeground, "#3b3b3b", "#6b6b6b", false, false, false, false, false, false),
            "Pmenu" => new HighlightStyle(Model.DefaultForeground, "#202020", "#555555", false, false, false, false, false, false),
            "PmenuSbar" => new HighlightStyle(null, "#2a2a2a", null, false, false, false, false, false, false),
            "PmenuThumb" => new HighlightStyle(null, "#6a6a6a", null, false, false, false, false, false, false),
            _ => null
        };
    }

    protected string ResolveForeground(HighlightStyle? style, string? fallback = null)
        => !string.IsNullOrWhiteSpace(style?.Foreground) ? style.Foreground! : fallback ?? Model?.DefaultForeground ?? "#d4d4d4";

    protected string ResolveBackground(HighlightStyle? style, string? fallback = null)
    {
        var background = !string.IsNullOrWhiteSpace(style?.Background)
            ? style.Background!
            : fallback ?? Model?.DefaultBackground ?? "#1e1e1e";
        return ApplyBlend(background, style, fallback ?? Model?.DefaultBackground ?? "#1e1e1e");
    }

    protected FormattedText MeasureOverlayText(string text)
        => CreateFormattedText(string.IsNullOrEmpty(text) ? " " : text, new Typeface(FontFamilyRef), Brushes.White);

    protected FormattedText CreateFormattedText(string text, HighlightStyle? style, IBrush brush)
        => CreateFormattedText(string.IsNullOrEmpty(text) ? " " : text, GetTypeface(style), brush);

    protected void DrawOverlayText(DrawingContext context, string text, Point point, IBrush brush, double opacity = 1, double xInset = 4)
    {
        var measurement = MeasureResolvedTextRuns(text, style: null);
        var y = point.Y + Math.Round((CellHeight - measurement.height) / 2);
        if (opacity >= 1)
        {
            DrawResolvedTextRuns(context, text, style: null, brush, new Point(point.X + xInset, y));
            return;
        }

        using (context.PushOpacity(opacity))
            DrawResolvedTextRuns(context, text, style: null, brush, new Point(point.X + xInset, y));
    }

    protected void DrawOverlayTextClipped(DrawingContext context, string text, Rect rect, IBrush brush, double xOffset = 0, double xInset = 0, double opacity = 1)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || string.IsNullOrEmpty(text))
            return;

        var measurement = MeasureResolvedTextRuns(text, style: null);
        var y = rect.Y + Math.Round((CellHeight - measurement.height) / 2);

        using (context.PushClip(rect))
        {
            if (opacity >= 1)
                DrawResolvedTextRuns(context, text, style: null, brush, new Point(rect.X + xInset + xOffset, y));
            else
            {
                using (context.PushOpacity(opacity))
                    DrawResolvedTextRuns(context, text, style: null, brush, new Point(rect.X + xInset + xOffset, y));
            }
        }
    }

    protected void DrawOverlayTextInRect(DrawingContext context, string text, Rect rect, IBrush brush, TextAlignment alignment, double opacity = 1)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || string.IsNullOrEmpty(text))
            return;

        var measurement = MeasureResolvedTextRuns(text, style: null);
        var x = alignment == TextAlignment.Right
            ? Math.Max(rect.X, rect.Right - measurement.width)
            : rect.X;
        var y = rect.Y + Math.Round((CellHeight - measurement.height) / 2);

        using (context.PushClip(rect))
        {
            if (opacity >= 1)
                DrawResolvedTextRuns(context, text, style: null, brush, new Point(x, y));
            else
            {
                using (context.PushOpacity(opacity))
                    DrawResolvedTextRuns(context, text, style: null, brush, new Point(x, y));
            }
        }
    }

    protected void DrawStyledChunksClipped(DrawingContext context, IReadOnlyList<MessageChunk> chunks, Rect rect, double xOffset = 0, double xInset = 0, double opacity = 1)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || chunks.Count == 0)
            return;

        using (context.PushClip(rect))
        {
            if (opacity < 1)
            {
                using (context.PushOpacity(opacity))
                    DrawChunks();
                return;
            }

            DrawChunks();
        }

        void DrawChunks()
        {
            var x = rect.X + xInset + xOffset;
            foreach (var chunk in chunks)
            {
                if (string.IsNullOrEmpty(chunk.Text))
                    continue;

                var (fg, _, style) = GetStyle(chunk.HlId);
                var brush = ToBrush(fg);
                var measurement = MeasureResolvedTextRuns(chunk.Text, style);
                var y = rect.Y + Math.Round((CellHeight - measurement.height) / 2);
                DrawResolvedTextRuns(context, chunk.Text, style, brush, new Point(x, y));
                DrawCellDecorations(context, x, rect.Y, measurement.width, fg, style);
                x += measurement.width;
            }
        }
    }

    protected double MeasureTextWidth(string text)
    {
        return MeasureResolvedTextRuns(text, style: null).width;
    }

    protected IReadOnlyList<MessageEntry> WrapMessageEntry(MessageEntry entry, double maxWidth)
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

    protected void DrawMessageEntry(DrawingContext context, MessageEntry entry, Rect rect)
    {
        var attentionHeader = "E325: ATTENTION";
        if (entry.Text.StartsWith(attentionHeader, StringComparison.Ordinal))
        {
            var codeBrush = Brushes.White;
            var codeBg = ToBrush("#8f2d2d");
            var codeText = MeasureOverlayText(attentionHeader);
            var y = rect.Y + Math.Round((CellHeight - codeText.Height) / 2);
            var codeRect = new Rect(rect.X + OverlayTextInset, rect.Y, codeText.Width + 4, rect.Height);
            context.FillRectangle(codeBg, codeRect);
            DrawResolvedTextRuns(context, attentionHeader, style: null, codeBrush, new Point(codeRect.X + 2, y));
            var rest = entry.Text.Substring(attentionHeader.Length);
            if (!string.IsNullOrEmpty(rest))
                DrawOverlayText(context, rest, new Point(codeRect.Right + 2, rect.Y), ToBrush(Model?.DefaultForeground ?? "#d4d4d4"), xInset: 0);
            return;
        }

        var chunks = entry.Chunks.Count > 0 ? entry.Chunks : [new MessageChunk(entry.Text, 0)];
        DrawStyledChunksClipped(context, chunks, rect, xInset: OverlayTextInset);
    }

    protected string GetMessageBackground(MessageEntry entry)
    {
        var baseBg = Model?.DefaultBackground ?? "#1e1e1e";
        return entry.Kind switch
        {
            "emsg" or "echoerr" => BlendColor(baseBg, "#7a1f1f", 0.28),
            "wmsg" => BlendColor(baseBg, "#7a5200", 0.22),
            "confirm" or "return_prompt" => baseBg,
            _ when entry.History => BlendColor(baseBg, "#ffffff", 0.10),
            _ => BlendColor(baseBg, "#ffffff", 0.08)
        };
    }

    protected bool HasConfirmMessages(IEnumerable<MessageEntry> entries)
        => entries.Any(entry => entry.Kind is "confirm" or "return_prompt");

    protected void DrawCellForeground(DrawingContext context, double x, double y, string text, string foreground, HighlightStyle? style, double width)
    {
        var brush = ToBrush(foreground);
        var measurement = MeasureResolvedTextRuns(text, style);
        // Terminal cells are positioned on a fixed grid. Centering wide glyphs
        // inside a 2-cell span creates visible gaps and cursor drift for CJK input.
        var textX = x;
        var clampedTextX = textX + measurement.width > Bounds.Width - 1
            ? Math.Max(0, Bounds.Width - measurement.width - 2)
            : textX;
        var textY = y + Math.Round((CellHeight - measurement.height) / 2);
        var clipRect = new Rect(x, y, Math.Max(1, width), CellHeight);
        using (context.PushClip(clipRect))
            DrawResolvedTextRuns(context, text, style, brush, new Point(clampedTextX, textY));
        DrawCellDecorations(context, x, y, width, foreground, style);
    }

    protected void DrawCellDecorations(DrawingContext context, double x, double y, double width, string foreground, HighlightStyle? style)
    {
        if (style is null)
            return;

        var specialBrush = ToBrush(style.Special ?? foreground);

        if (style.Underline)
        {
            var underlineY = y + CellHeight - 2;
            context.DrawLine(new Pen(specialBrush, 1), new Point(x, underlineY), new Point(x + width, underlineY));
        }

        if (style.Undercurl)
        {
            var baseY = y + CellHeight - 2;
            var points = new List<Point>();
            var step = Math.Max(3, CellWidth / 3);
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
            var strikeY = y + (CellHeight / 2);
            context.DrawLine(new Pen(specialBrush, 1), new Point(x, strikeY), new Point(x + width, strikeY));
        }
    }

    protected void DrawShadow(DrawingContext context, Rect rect, double spread, double opacity)
    {
        var shadowRect = rect.Inflate(spread);
        using (context.PushOpacity(opacity))
            context.FillRectangle(Brushes.Black, shadowRect, 6);
    }

    protected string GetFloatingGridBackground(GridState grid)
    {
        for (var row = 0; row < grid.Cells.Length; row++)
        {
            for (var col = 0; col < grid.Cells[row].Length; col++)
            {
                var cell = grid.Cells[row][col];
                var (_, bg, _) = GetStyle(cell.Hl);
                if (!string.Equals(bg, Model?.DefaultBackground, StringComparison.OrdinalIgnoreCase))
                    return bg;
            }
        }

        return Model?.DefaultBackground ?? "#1e1e1e";
    }

    protected static string BlendColor(string baseColor, string overlayColor, double overlayRatio)
    {
        overlayRatio = Math.Clamp(overlayRatio, 0, 1);

        try
        {
            var baseParsed = Color.Parse(baseColor);
            var overlayParsed = Color.Parse(overlayColor);
            byte Mix(byte a, byte b) => (byte)Math.Clamp(Math.Round((a * (1 - overlayRatio)) + (b * overlayRatio)), 0, 255);
            return $"#{Mix(baseParsed.R, overlayParsed.R):x2}{Mix(baseParsed.G, overlayParsed.G):x2}{Mix(baseParsed.B, overlayParsed.B):x2}";
        }
        catch
        {
            return baseColor;
        }
    }

    protected static IBrush ToBrush(string? color)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(color))
                return Brush.Parse(color);
        }
        catch (Exception ex)
        {
            GuiLogger.Warn(GuiLogCategory.Render, () => $"Invalid brush color={color} error={ex.Message}");
        }

        return Brushes.Transparent;
    }

    private FormattedText CreateFormattedText(string text, Typeface typeface, IBrush brush)
        => new(
            string.IsNullOrEmpty(text) ? " " : text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            brush);

    protected (double width, double height) MeasureResolvedTextRuns(string text, HighlightStyle? style)
    {
        var runs = ResolveTextRuns(string.IsNullOrEmpty(text) ? " " : text, style);
        var width = 0d;
        var height = 0d;
        foreach (var run in runs)
        {
            var formatted = CreateFormattedText(run.Text, run.Typeface, Brushes.White);
            width += formatted.Width;
            height = Math.Max(height, formatted.Height);
        }

        return (width, Math.Max(1, height));
    }

    protected void DrawResolvedTextRuns(DrawingContext context, string text, HighlightStyle? style, IBrush brush, Point origin)
    {
        var runs = ResolveTextRuns(string.IsNullOrEmpty(text) ? " " : text, style);
        var x = origin.X;
        foreach (var run in runs)
        {
            var formatted = CreateFormattedText(run.Text, run.Typeface, brush);
            context.DrawText(formatted, new Point(x, origin.Y));
            x += formatted.Width;
        }
    }

    private IReadOnlyList<ResolvedTextRun> ResolveTextRuns(string text, HighlightStyle? style)
    {
        var baseTypeface = GetTypeface(style);
        var runs = new List<ResolvedTextRun>();
        var currentTypeface = ResolveTypefaceForRune(text.EnumerateRunes().First(), baseTypeface);
        var current = new StringBuilder();

        foreach (var rune in text.EnumerateRunes())
        {
            var resolved = ResolveTypefaceForRune(rune, baseTypeface);
            if (!SameTypeface(currentTypeface, resolved) && current.Length > 0)
            {
                runs.Add(new ResolvedTextRun(current.ToString(), currentTypeface));
                current.Clear();
                currentTypeface = resolved;
            }
            else if (current.Length == 0)
            {
                currentTypeface = resolved;
            }

            current.Append(rune.ToString());
        }

        if (current.Length > 0)
            runs.Add(new ResolvedTextRun(current.ToString(), currentTypeface));

        return runs;
    }

    protected string DescribeResolvedTextRuns(string text, HighlightStyle? style)
    {
        var runs = ResolveTextRuns(text, style);
        return string.Join(
            " | ",
            runs.Select(run => $"{run.Typeface.FontFamily.Name}:{run.Text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}"));
    }

    private Typeface ResolveTypefaceForRune(Rune rune, Typeface baseTypeface)
    {
        var key = $"{baseTypeface.FontFamily.Name}|{baseTypeface.Style}|{baseTypeface.Weight}|{baseTypeface.Stretch}|{rune.Value}";
        if (TypefaceMatchCache.TryGetValue(key, out var cached))
            return cached;

        // Nerd Font / Powerline glyphs usually live in Unicode private-use areas.
        // Matching them per-character can incorrectly fall through to symbol fonts
        // like OpenSymbol, so prefer the user-selected GUI font family chain first.
        if (IsPrivateUseRune(rune))
        {
            TypefaceMatchCache[key] = baseTypeface;
            return baseTypeface;
        }

        if (FontManager.Current.TryMatchCharacter(
                rune.Value,
                baseTypeface.Style,
                baseTypeface.Weight,
                baseTypeface.Stretch,
                baseTypeface.FontFamily,
                CultureInfo.InvariantCulture,
                out var matched))
        {
            TypefaceMatchCache[key] = matched;
            return matched;
        }

        TypefaceMatchCache[key] = baseTypeface;
        return baseTypeface;
    }

    private static bool IsPrivateUseRune(Rune rune)
        => (rune.Value >= 0xE000 && rune.Value <= 0xF8FF)
           || (rune.Value >= 0xF0000 && rune.Value <= 0xFFFFD)
           || (rune.Value >= 0x100000 && rune.Value <= 0x10FFFD);

    private static bool SameTypeface(Typeface left, Typeface right)
        => string.Equals(left.FontFamily.Name, right.FontFamily.Name, StringComparison.OrdinalIgnoreCase)
           && left.Style == right.Style
           && left.Weight == right.Weight
           && left.Stretch == right.Stretch;

    private EditorRenderMetrics ComputeMetrics()
    {
        if (UseFixedCellMetrics)
        {
            return new EditorRenderMetrics(
                FontFamilyName,
                FontSize,
                LineHeight,
                new FontFamily(FontFamilyName),
                Math.Max(1, FixedCellWidth),
                Math.Max(1, FixedCellHeight),
                Math.Max(1, FixedCellWidth),
                Math.Max(1, FixedCellWidth),
                Math.Max(1, FixedCellHeight),
                Math.Max(1, FixedCellHeight));
        }

        var cacheKey = $"{FontFamilyName}|{FontSize:F3}|{LineHeight:F3}";
        if (MetricsCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var fontFamily = new FontFamily(FontFamilyName);
        var probeTypeface = new Typeface(fontFamily);
        var narrowProbe = CreateFormattedText("0", probeTypeface, Brushes.White);
        var asciiProbe = CreateFormattedText("abcdefghijklmnopqrstuvwxyz", probeTypeface, Brushes.White);
        var wideProbe = CreateFormattedText("あ", probeTypeface, Brushes.White);
        var narrowWidth = Math.Max(narrowProbe.Width, asciiProbe.Width / 26d);
        var wideHalfWidth = wideProbe.Width / 2d;
        var measuredTextHeight = Math.Max(narrowProbe.Height, asciiProbe.Height);
        var cellWidth = Math.Max(1, Math.Round(Math.Max(narrowWidth, wideHalfWidth), 2));
        var cellHeight = Math.Max(1, Math.Ceiling(measuredTextHeight * Math.Max(1.0d, LineHeight)));
        var metrics = new EditorRenderMetrics(
            FontFamilyName,
            FontSize,
            LineHeight,
            fontFamily,
            cellWidth,
            cellHeight,
            narrowWidth,
            wideHalfWidth,
            narrowProbe.Height,
            wideProbe.Height);

        MetricsCache[cacheKey] = metrics;
        GuiLogger.Info(
            GuiLogCategory.Render,
            () => $"MeasureCell font={FontFamilyName} fontSize={FontSize:F1} lineHeight={LineHeight:F2} narrowWidth={narrowWidth:F2} wideHalfWidth={wideHalfWidth:F2} narrowHeight={narrowProbe.Height:F2} wideHeight={wideProbe.Height:F2} asciiCell={cellWidth:F2} wideGlyph={wideProbe.Width:F2} wideRatio={(wideProbe.Width / Math.Max(1, cellWidth)):F2} cellHeight={cellHeight:F2}");
        return metrics;
    }

    private string ApplyBlend(string background, HighlightStyle? style, string fallbackBackground)
    {
        if (style?.Blend is not > 0)
            return background;
        return BlendColor(background, fallbackBackground, style.Blend / 100d);
    }

    private string ReadableForegroundForBackground(string background)
    {
        try
        {
            var bg = Color.Parse(background);
            var luminance = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255d;
            return luminance > 0.5 ? "#000000" : "#ffffff";
        }
        catch
        {
            return Model?.DefaultForeground ?? "#d4d4d4";
        }
    }

    private int GetPopupFirstIndex(int selected, int totalCount, int visibleCount, int previousFirstIndex, int previousLastSelectedIndex)
    {
        if (visibleCount >= totalCount)
            return 0;

        var clampedSelected = Math.Clamp(selected, 0, totalCount - 1);
        var current = Math.Clamp(previousFirstIndex, 0, Math.Max(0, totalCount - visibleCount));
        if (previousLastSelectedIndex < 0)
            return Math.Clamp(clampedSelected - (visibleCount / 2), 0, totalCount - visibleCount);
        if (clampedSelected < current)
            return clampedSelected;
        if (clampedSelected >= current + visibleCount)
            return Math.Clamp(clampedSelected - visibleCount + 1, 0, totalCount - visibleCount);
        return current;
    }
}

public readonly record struct PopupMenuLayout(
    Rect Rect,
    int FirstIndex,
    int VisibleCount,
    double WordColumnWidth,
    double KindWidth,
    double ExtraWidth,
    double GapWidth,
    double ScrollbarWidth);

internal readonly record struct ResolvedTextRun(string Text, Typeface Typeface);
