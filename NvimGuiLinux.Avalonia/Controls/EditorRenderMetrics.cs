using Avalonia.Media;

namespace NvimGuiLinux.Avalonia.Controls;

internal sealed record EditorRenderMetrics(
    string FontFamilyName,
    double FontSize,
    double LineHeight,
    FontFamily FontFamily,
    double CellWidth,
    double CellHeight,
    double NarrowWidth,
    double WideHalfWidth,
    double NarrowHeight,
    double WideHeight);
