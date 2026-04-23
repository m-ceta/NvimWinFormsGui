namespace NvimGuiCommon.Editor;
public sealed record HighlightStyle(
    string? Foreground,
    string? Background,
    string? Special,
    bool Reverse,
    bool Bold,
    bool Italic,
    bool Underline,
    bool Undercurl,
    bool Strikethrough);
