using NvimGuiCommon.Nvim;

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
    }

    public LineGridModel Grid { get; }
    public event Action<string>? Stderr;

    public async Task StartAsync()
    {
        await _session.StartAsync();
        await _session.AttachUiAsync(_cols, _rows);
    }

    public Task ResizeAsync(int cols, int rows)
    {
        _cols = cols;
        _rows = rows;
        return _session.ResizeAsync(cols, rows);
    }

    public Task InputAsync(string input, bool isTermcode) => _session.InputAsync(input, isTermcode);
    public Task MouseAsync(string button, string action, string modifiers, int grid, int row, int col) => _session.MouseAsync(button, action, modifiers, grid, row, col);
    public Task SaveAsync() => _session.CommandAsync("write");
    public Task SaveAsAsync(string path) => _session.CommandAsync($"saveas {EscapeEx(path)}");
    public Task CloseBufferAsync() => _session.CommandAsync("bdelete");
    public Task<string?> GetCurrentBufferPathAsync() => _session.GetCurrentBufferPathAsync();
    public Task DiffSplitAsync(string path) => _session.CommandAsync($"vert diffsplit {EscapeEx(path)}");

    public Task OpenFileAsync(string fullPath, OpenMode mode)
    {
        var cmd = mode switch
        {
            OpenMode.NewTab => $"tabedit {EscapeEx(fullPath)}",
            OpenMode.Current => $"edit {EscapeEx(fullPath)}",
            OpenMode.VSplit => $"vsplit {EscapeEx(fullPath)}",
            OpenMode.Split => $"split {EscapeEx(fullPath)}",
            _ => $"edit {EscapeEx(fullPath)}",
        };
        return _session.CommandAsync(cmd);
    }

    private static string EscapeEx(string path)
        => path.Replace(@"\", @"\\").Replace(" ", @"\ ").Replace("|", @"\|");

    public void Dispose() => _session.Dispose();
}
