using NvimGuiCommon.Nvim;
using NvimGuiCommon.Diagnostics;

namespace NvimGuiCommon.Editor;

public sealed class EditorController : IDisposable
{
    private readonly NvimSession _session = new();
    private int _cols = 100;
    private int _rows = 35;

    public EditorController()
    {
        Grid = new LineGridModel();
        _session.Redraw += events => Grid.ApplyRedraw(events);
        _session.Stderr += s => Stderr?.Invoke(s);
        _session.Faulted += ex => GuiLogger.Error(GuiLogCategory.RedrawEvent, () => $"NvimSession faulted error={ex}");
    }

    public LineGridModel Grid { get; }
    public event Action<string>? Stderr;

    public async Task StartAsync()
    {
        GuiLogger.Info(GuiLogCategory.Performance, () => $"EditorController.StartAsync begin cols={_cols} rows={_rows}");
        await _session.StartAsync();
        GuiLogger.Info(GuiLogCategory.Performance, () => "EditorController.StartAsync session started");
        await _session.AttachUiAsync(_cols, _rows);
        GuiLogger.Info(GuiLogCategory.Performance, () => "EditorController.StartAsync ui attached");
        await _session.CommandAsync("set guicursor=n-v-c:block,i-ci-ve:ver25,r-cr:hor20,o:hor50");
        await _session.CommandAsync("set laststatus=2");
        await _session.CommandAsync("set mouse=a");
        await _session.CommandAsync("redrawstatus!");
        GuiLogger.Info(GuiLogCategory.Performance, () => "EditorController.StartAsync commands completed");
    }

    public Task ResizeAsync(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        GuiLogger.Debug(GuiLogCategory.Resize, () => $"EditorController.ResizeAsync cols={cols} rows={rows}");
        return _session.ResizeAsync(cols, rows);
    }

    public Task InputAsync(string input, bool isTermcode)
    {
        GuiLogger.Debug(GuiLogCategory.Keyboard, () => $"EditorController.InputAsync data={Sanitize(input)} termcode={isTermcode}");
        return _session.InputAsync(input, isTermcode);
    }

    public Task MouseAsync(string button, string action, string modifiers, int grid, int row, int col)
    {
        GuiLogger.Debug(GuiLogCategory.Mouse, () => $"EditorController.MouseAsync button={button} action={action} modifiers={modifiers} grid={grid} row={row} col={col}");
        return _session.MouseAsync(button, action, modifiers, grid, row, col);
    }
    public Task SaveAsync() => _session.CommandAsync("write");
    public Task SaveAsAsync(string path) => _session.CommandAsync($"saveas {EscapeEx(path)}");
    public Task CloseBufferAsync() => _session.CommandAsync("bdelete");
    public Task SwitchTabAsync(int index) => index > 0 ? _session.CommandAsync($"tabnext {index}") : Task.CompletedTask;
    public Task CloseTabAsync(int index) => index > 0 ? _session.CommandAsync($"tabclose {index}") : Task.CompletedTask;
    public Task<string?> GetCurrentBufferPathAsync() => _session.GetCurrentBufferPathAsync();
    public Task DiffSplitAsync(string path) => _session.CommandAsync($"vert diffsplit {EscapeEx(path)}");

    public async Task OpenFileAsync(string fullPath, OpenMode mode)
    {
        var cmd = mode switch
        {
            OpenMode.NewTab => $"tabedit {EscapeEx(fullPath)}",
            OpenMode.Current => $"edit {EscapeEx(fullPath)}",
            OpenMode.VSplit => $"vsplit {EscapeEx(fullPath)}",
            OpenMode.Split => $"split {EscapeEx(fullPath)}",
            _ => $"edit {EscapeEx(fullPath)}",
        };
        await _session.CommandAsync(cmd);
        await _session.CommandAsync("redrawstatus!");
        await _session.CommandAsync("redraw!");
    }

    private static string EscapeEx(string path)
        => path.Replace(@"\", @"\\").Replace(" ", @"\ ").Replace("|", @"\|");

    private static string Sanitize(string value)
        => value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    public void Dispose() => _session.Dispose();
}
