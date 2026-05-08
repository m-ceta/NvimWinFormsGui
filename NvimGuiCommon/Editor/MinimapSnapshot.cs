namespace NvimGuiCommon.Editor;

public sealed record MinimapLineInfo(
    int Rows,
    int Width,
    int? Start,
    int? End,
    string? Color);

public sealed record MinimapSnapshot(
    int LineCount,
    int MaxColumn,
    int TotalDisplayRows,
    int TextWidth,
    int TopLine,
    int BottomLine,
    int CurrentLine,
    int CurrentColumn,
    int SkipColumn,
    IReadOnlyList<MinimapLineInfo> Lines);
