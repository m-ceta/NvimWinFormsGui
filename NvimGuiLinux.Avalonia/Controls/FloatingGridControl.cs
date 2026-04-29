using Avalonia.Media;
using NvimGuiCommon.Editor;

namespace NvimGuiLinux.Avalonia.Controls;

public sealed class FloatingGridControl : EditorLayerControl
{
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Model is null)
            return;

        MeasureCell();
        foreach (var grid in Model.VisibleGrids.Where(g => g.Floating))
            RenderGrid(context, grid, drawShadow: true);

        if (Model.Grids.TryGetValue(Model.CursorGrid, out var cursorGrid) && cursorGrid.Visible && cursorGrid.Floating)
            RenderCursorOnGrid(context, cursorGrid);
    }
}
