namespace NvimGuiCommon.Editor;
public sealed class GridCell
{
    public string Ch { get; set; } = " ";
    public int Hl { get; set; }
    public bool Continue { get; set; }

    public GridCell Clone() => new() { Ch = Ch, Hl = Hl, Continue = Continue };
}
