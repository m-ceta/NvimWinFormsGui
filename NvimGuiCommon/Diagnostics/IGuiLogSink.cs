namespace NvimGuiCommon.Diagnostics;

public interface IGuiLogSink : IDisposable
{
    void Write(in GuiLogEntry entry);
}
