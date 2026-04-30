using Avalonia.Media;

namespace NvimGuiLinux.Avalonia.Controls;

public sealed class PopupMenuControl : EditorLayerControl
{
    private int _popupFirstIndex;
    private int _popupLastSelectedIndex = -1;

    public PopupMenuControl()
    {
        Focusable = false;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Model?.PopupMenuState is not { HasItems: true } popup)
        {
            _popupFirstIndex = 0;
            _popupLastSelectedIndex = -1;
            return;
        }

        var popupLayout = CalculatePopupMenuLayout(popup, GetCmdlineRect(), _popupFirstIndex, _popupLastSelectedIndex);
        NvimGuiCommon.Diagnostics.GuiLogger.Debug(NvimGuiCommon.Diagnostics.GuiLogCategory.PopupMenu, () => $"PopupMenuLayer bounds=x={popupLayout.Rect.X:F1},y={popupLayout.Rect.Y:F1},w={popupLayout.Rect.Width:F1},h={popupLayout.Rect.Height:F1} selected={popup.Selected} items={popup.Items.Count} grid={popup.Grid} row={popup.Row} col={popup.Col}");
        DrawPopupMenu(context, popup, popupLayout);
        _popupFirstIndex = popupLayout.FirstIndex;
        _popupLastSelectedIndex = popup.Selected;
    }
}
