using Avalonia;
using Avalonia.Media;
using NvimGuiCommon.Editor;
using System.Text;

namespace NvimGuiLinux.Avalonia.Controls;

public sealed class CmdlineControl : EditorLayerControl
{
    private double _scrollX;

    public CmdlineControl()
    {
        Focusable = false;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Model is null)
            return;

        MeasureCell();
        RenderCmdlineBlock(context);
        if (Model.ActiveCmdline is { } cmdline)
            RenderCmdline(context, cmdline);
        else
            _scrollX = 0;
    }

    private void RenderCmdline(DrawingContext context, CmdlineState cmdline)
    {
        var model = Model;
        var rect = GetCmdlineRect();
        if (model is null || rect is not Rect visibleRect)
            return;

        var prefix = $"{cmdline.FirstChar}{cmdline.Prompt}{new string(' ', Math.Max(0, cmdline.Indent))}";
        var bodyRunes = BuildBodyRunes(cmdline);
        var bodyCursor = Math.Max(0, Math.Min(bodyRunes.Count, cmdline.Position));
        ApplySpecialChar(cmdline, bodyRunes, bodyCursor);

        var prefixRunes = prefix.EnumerateRunes().Select(r => new StyledRune(r.ToString(), 0)).ToList();
        var fullRunes = prefixRunes.Concat(bodyRunes).ToList();
        var cursorIndex = Math.Max(0, Math.Min(fullRunes.Count, prefixRunes.Count + bodyCursor));
        var wrapPrompt = string.IsNullOrEmpty(cmdline.FirstChar) && !string.IsNullOrEmpty(cmdline.Prompt) && bodyRunes.Count == 0;
        var text = string.Concat(fullRunes.Select(r => r.Text));
        NvimGuiCommon.Diagnostics.GuiLogger.Debug(NvimGuiCommon.Diagnostics.GuiLogCategory.Cmdline, () => $"CmdlineLayer bounds=x={visibleRect.X:F1},y={visibleRect.Y:F1},w={visibleRect.Width:F1},h={visibleRect.Height:F1} text={text} cursorCol={cursorIndex}");

        context.FillRectangle(ToBrush(model.DefaultBackground), visibleRect);

        var viewportWidth = Math.Max(1, visibleRect.Width - (OverlayTextInset * 2));
        var cursorLeft = MeasureRuneWidth(fullRunes.Take(cursorIndex));
        var cursorChar = cursorIndex >= 0 && cursorIndex < fullRunes.Count ? fullRunes[cursorIndex].Text : " ";
        var cursorWidth = Math.Max(CellWidth, MeasureTextWidth(cursorChar));
        var cursorRight = cursorLeft + cursorWidth;

        if (wrapPrompt)
        {
            _scrollX = 0;
        }
        else
        {
            var viewportLeft = _scrollX;
            var viewportRight = viewportLeft + viewportWidth;
            if (cursorRight > viewportRight)
                _scrollX = Math.Max(0, cursorRight - viewportWidth);
            else if (cursorLeft < viewportLeft)
                _scrollX = Math.Max(0, cursorLeft);
        }

        var drawableChunks = CollapseRunes(fullRunes);
        DrawStyledChunksClipped(context, drawableChunks, visibleRect, -_scrollX, OverlayTextInset);

        var cursorX = visibleRect.X + OverlayTextInset + cursorLeft - _scrollX;
        var caretRect = new Rect(cursorX, visibleRect.Y + 3, Math.Max(2, Math.Round(CellWidth * 0.14d)), Math.Max(1, visibleRect.Height - 6));
        context.FillRectangle(ToBrush(model.DefaultForeground), caretRect);

        if (cmdline.Level > 1)
        {
            var badgeRect = new Rect(visibleRect.Right - (CellWidth * 5), visibleRect.Y, CellWidth * 5, visibleRect.Height);
            DrawOverlayTextInRect(context, $"[{cmdline.Level}]", badgeRect, ToBrush(BlendColor(model.DefaultForeground, model.DefaultBackground, 0.35)), TextAlignment.Right, 0.75);
        }
    }

    private void RenderCmdlineBlock(DrawingContext context)
    {
        if (Model is null || Model.CmdlineBlock.Count == 0)
            return;

        var maxVisibleLines = Math.Max(2, (int)Math.Floor((Bounds.Height * 0.4) / CellHeight));
        var lines = Model.CmdlineBlock.TakeLast(Math.Max(1, maxVisibleLines)).ToArray();
        var offsetRows = GetStatuslineOffsetRows() + (Model.ActiveCmdline is null ? 0 : 1);
        var rect = BottomOverlayRect(lines.Length, offsetRows);
        context.FillRectangle(ToBrush(Model.DefaultBackground), rect);
        for (var i = 0; i < lines.Length; i++)
        {
            var lineRect = new Rect(rect.X, rect.Y + (i * CellHeight), rect.Width, CellHeight);
            DrawOverlayTextClipped(context, lines[i], lineRect, ToBrush(Model.DefaultForeground), xInset: OverlayTextInset);
        }
    }

    private List<StyledRune> BuildBodyRunes(CmdlineState cmdline)
    {
        var chunks = cmdline.Chunks.Count > 0 ? cmdline.Chunks : [new MessageChunk(cmdline.Text, 0)];
        var bodyRunes = new List<StyledRune>();
        foreach (var chunk in chunks)
        {
            foreach (var rune in chunk.Text.EnumerateRunes())
                bodyRunes.Add(new StyledRune(rune.ToString(), chunk.HlId));
        }
        return bodyRunes;
    }

    private static void ApplySpecialChar(CmdlineState cmdline, List<StyledRune> bodyRunes, int bodyCursor)
    {
        if (string.IsNullOrEmpty(cmdline.SpecialChar))
            return;

        var specialRune = new StyledRune(cmdline.SpecialChar, bodyCursor < bodyRunes.Count ? bodyRunes[bodyCursor].HlId : 0);
        if (cmdline.SpecialShift)
            bodyRunes.Insert(bodyCursor, specialRune);
        else if (bodyCursor < bodyRunes.Count)
            bodyRunes[bodyCursor] = specialRune;
        else
            bodyRunes.Add(specialRune);
    }

    private static IReadOnlyList<MessageChunk> CollapseRunes(IEnumerable<StyledRune> runes)
    {
        var result = new List<MessageChunk>();
        var current = new StringBuilder();
        var currentHl = int.MinValue;

        foreach (var rune in runes)
        {
            if (currentHl == rune.HlId || currentHl == int.MinValue)
            {
                currentHl = rune.HlId;
                current.Append(rune.Text);
                continue;
            }

            result.Add(new MessageChunk(current.ToString(), currentHl));
            current.Clear();
            currentHl = rune.HlId;
            current.Append(rune.Text);
        }

        if (current.Length > 0)
            result.Add(new MessageChunk(current.ToString(), currentHl == int.MinValue ? 0 : currentHl));

        return result;
    }

    private double MeasureRuneWidth(IEnumerable<StyledRune> runes)
        => runes.Sum(rune => MeasureTextWidth(rune.Text));

    private readonly record struct StyledRune(string Text, int HlId);
}
