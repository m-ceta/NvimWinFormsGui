using Avalonia;
using NvimGuiCommon.Editor;

namespace NvimGuiLinux.Avalonia.Controls;

public readonly record struct EditorLayoutSnapshot(
    double CellWidth,
    double CellHeight,
    double EditorTopInset,
    double BottomOverlayTop,
    double BoundsWidth,
    double BoundsHeight)
{
    public double GetGridTop(GridState grid)
    {
        var row = grid.RenderRow;
        if (grid.Floating && (string.Equals(grid.FloatAnchor, "SW", StringComparison.OrdinalIgnoreCase) || string.Equals(grid.FloatAnchor, "SE", StringComparison.OrdinalIgnoreCase)))
            row -= Math.Max(0, grid.Rows - 1);
        return EditorTopInset + (row * CellHeight);
    }

    public double GetGridLeft(GridState grid)
    {
        var col = grid.RenderCol;
        if (grid.Floating && (string.Equals(grid.FloatAnchor, "NE", StringComparison.OrdinalIgnoreCase) || string.Equals(grid.FloatAnchor, "SE", StringComparison.OrdinalIgnoreCase)))
            col -= Math.Max(0, grid.Cols - 1);
        var left = col * CellWidth;
        if (grid.Floating)
            left = Math.Clamp(left, 0, Math.Max(0, BoundsWidth - (grid.Cols * CellWidth)));
        return left;
    }

    public Rect GetGridRect(GridState grid)
        => new(GetGridLeft(grid), GetGridTop(grid), grid.Cols * CellWidth, grid.Rows * CellHeight);

    public Rect BottomOverlayRect(int heightInRows, int offsetRows)
    {
        var height = Math.Max(1, heightInRows * CellHeight);
        var y = Math.Max(EditorTopInset, BottomOverlayTop - (offsetRows * CellHeight) - ((heightInRows - 1) * CellHeight));
        return new Rect(0, y, BoundsWidth, height);
    }
}
