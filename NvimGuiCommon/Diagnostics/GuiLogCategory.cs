namespace NvimGuiCommon.Diagnostics;

[Flags]
public enum GuiLogCategory : ulong
{
    None = 0,
    Layout = 1UL << 0,
    Resize = 1UL << 1,
    Render = 1UL << 2,
    RedrawEvent = 1UL << 3,
    Cmdline = 1UL << 4,
    Message = 1UL << 5,
    PopupMenu = 1UL << 6,
    FloatingGrid = 1UL << 7,
    Mouse = 1UL << 8,
    Keyboard = 1UL << 9,
    TextInput = 1UL << 10,
    Focus = 1UL << 11,
    FolderTree = 1UL << 12,
    MainMenu = 1UL << 13,
    ContextMenu = 1UL << 14,
    Performance = 1UL << 15,
    All = ulong.MaxValue
}
