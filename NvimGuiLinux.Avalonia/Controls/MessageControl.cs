using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using NvimGuiCommon.Editor;

namespace NvimGuiLinux.Avalonia.Controls;

public sealed class MessageControl : EditorLayerControl
{
    private LineGridModel? _boundModel;
    private Action? _messageChangedHandler;
    private CancellationTokenSource? _hideCts;
    private int _observedTransientGeneration = -1;
    private int _hiddenTransientGeneration = -1;

    public MessageControl()
    {
        Focusable = false;
    }

    public override void Bind(LineGridModel model)
    {
        if (_boundModel is not null && _messageChangedHandler is not null)
            _boundModel.Changed -= _messageChangedHandler;

        base.Bind(model);
        _boundModel = model;
        _messageChangedHandler = () => Dispatcher.UIThread.Post(HandleModelChanged);
        _boundModel.Changed += _messageChangedHandler;
        HandleModelChanged();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Model is null)
            return;

        MeasureCell();

        var activeEntries = Model.HistoryVisible ? Model.HistoryEntries : Model.MessageEntries;
        var hideTransientMessages = !Model.HistoryVisible
            && _hiddenTransientGeneration == Model.TransientMessageGeneration;
        var showStatusLine = Model.ActiveCmdline is null
            && (!string.IsNullOrWhiteSpace(Model.ShowMode)
                || !string.IsNullOrWhiteSpace(Model.ShowCommand)
                || !string.IsNullOrWhiteSpace(Model.Ruler));
        var showMessageLinesDuringCmdline = Model.HistoryVisible || Model.MessageGridRow.HasValue || HasConfirmMessages(activeEntries);
        var visibleEntries = Model.ActiveCmdline is not null
            ? (showMessageLinesDuringCmdline && !hideTransientMessages ? activeEntries.ToArray() : Array.Empty<MessageEntry>())
            : (Model.HistoryVisible
                ? activeEntries.ToArray()
                : hideTransientMessages
                    ? Array.Empty<MessageEntry>()
                    : activeEntries.TakeLast(Math.Min(activeEntries.Count, 8)).ToArray());

        if (visibleEntries.Length == 0 && !showStatusLine)
            return;

        var wrappedLines = visibleEntries
            .SelectMany(entry => WrapMessageEntry(entry, Math.Max(1, Bounds.Width - (OverlayTextInset * 2))))
            .ToArray();
        var messageGridAnchored = Model.MessageGridRow.HasValue && wrappedLines.Length > 0;

        if (!messageGridAnchored)
            wrappedLines = TrimLinesForViewport(wrappedLines, HasConfirmMessages(visibleEntries));

        var totalRows = wrappedLines.Length + (showStatusLine && !messageGridAnchored ? 1 : 0);
        if (totalRows == 0)
            return;

        var rect = messageGridAnchored
            ? new Rect(0, GetEditorTopInset() + (Model.MessageGridRow!.Value * CellHeight), Bounds.Width, wrappedLines.Length * CellHeight)
            : BottomOverlayRect(totalRows, GetStatuslineOffsetRows() + GetOverlayBottomInsetRows());
        NvimGuiCommon.Diagnostics.GuiLogger.Debug(NvimGuiCommon.Diagnostics.GuiLogCategory.Message, () => $"MessageLayer bounds=x={rect.X:F1},y={rect.Y:F1},w={rect.Width:F1},h={rect.Height:F1} lines={wrappedLines.Length}");

        for (var i = 0; i < wrappedLines.Length; i++)
        {
            var entry = wrappedLines[i];
            var lineRect = new Rect(rect.X, rect.Y + (i * CellHeight), rect.Width, CellHeight);
            context.FillRectangle(ToBrush(GetMessageBackground(entry)), lineRect);
            DrawMessageEntry(context, entry, lineRect);
        }

        if (showStatusLine && !messageGridAnchored)
        {
            var statusRect = new Rect(rect.X, rect.Bottom - CellHeight, rect.Width, CellHeight);
            context.FillRectangle(ToBrush(Model.DefaultBackground), statusRect);
            DrawStatusLine(context, statusRect);
        }
    }

    private MessageEntry[] TrimLinesForViewport(MessageEntry[] wrappedLines, bool hasConfirmMessages)
    {
        if (wrappedLines.Length == 0)
            return wrappedLines;

        var maxRows = hasConfirmMessages
            ? Math.Max(6, (int)Math.Floor((Bounds.Height - GetEditorTopInset() - ((GetStatuslineOffsetRows() + GetOverlayBottomInsetRows()) * CellHeight)) / Math.Max(1, CellHeight)))
            : Math.Max(2, (int)Math.Floor((Bounds.Height * 0.33) / Math.Max(1, CellHeight)));

        return wrappedLines.Length <= maxRows
            ? wrappedLines
            : wrappedLines.TakeLast(maxRows).ToArray();
    }

    private void DrawStatusLine(DrawingContext context, Rect statusRect)
    {
        if (Model is null)
            return;

        var gap = CellWidth;
        var paddedRect = new Rect(statusRect.X + OverlayTextInset, statusRect.Y, Math.Max(0, statusRect.Width - (OverlayTextInset * 2)), statusRect.Height);
        var rightWidth = Math.Max(0, MeasureOverlayText(string.IsNullOrEmpty(Model.Ruler) ? " " : Model.Ruler).Width);
        var leftAndMidWidth = Math.Max(0, paddedRect.Width - rightWidth - (string.IsNullOrWhiteSpace(Model.Ruler) ? 0 : gap));
        var leftWidth = Math.Max(0, (leftAndMidWidth - gap) / 2);
        var midWidth = Math.Max(0, leftAndMidWidth - leftWidth - gap);

        var leftRect = new Rect(paddedRect.X, paddedRect.Y, leftWidth, paddedRect.Height);
        var midRect = new Rect(leftRect.Right + gap, paddedRect.Y, midWidth, paddedRect.Height);
        var rightRect = new Rect(Math.Max(midRect.Right + gap, paddedRect.Right - rightWidth), paddedRect.Y, Math.Max(0, paddedRect.Right - Math.Max(midRect.Right + gap, paddedRect.Right - rightWidth)), paddedRect.Height);

        DrawOverlayTextInRect(context, Model.ShowMode, leftRect, ToBrush(Model.DefaultForeground), TextAlignment.Left);
        DrawOverlayTextInRect(context, Model.ShowCommand, midRect, ToBrush(Model.DefaultForeground), TextAlignment.Right);
        DrawOverlayTextInRect(context, Model.Ruler, rightRect, ToBrush(Model.DefaultForeground), TextAlignment.Right);
    }

    private void HandleModelChanged()
    {
        if (Model is null)
            return;

        if (Model.HistoryVisible || Model.MessageEntries.Count == 0)
        {
            _hideCts?.Cancel();
            _hiddenTransientGeneration = -1;
            _observedTransientGeneration = Model.TransientMessageGeneration;
            return;
        }

        if (_observedTransientGeneration == Model.TransientMessageGeneration)
            return;

        _observedTransientGeneration = Model.TransientMessageGeneration;
        _hiddenTransientGeneration = -1;
        _hideCts?.Cancel();
        _hideCts?.Dispose();
        _hideCts = new CancellationTokenSource();
        var generation = _observedTransientGeneration;
        var token = _hideCts.Token;

        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), token);
                if (token.IsCancellationRequested || Model is null || Model.HistoryVisible)
                    return;

                if (Model.TransientMessageGeneration != generation)
                    return;

                _hiddenTransientGeneration = generation;
                InvalidateVisual();
            }
            catch (OperationCanceledException)
            {
            }
        });

        InvalidateVisual();
    }
}
