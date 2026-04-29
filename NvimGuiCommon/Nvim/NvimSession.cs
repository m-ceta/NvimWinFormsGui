using NvimGuiCommon.Config;
using NvimGuiCommon.Diagnostics;
using System.Diagnostics;

namespace NvimGuiCommon.Nvim;

public sealed class NvimSession : IDisposable
{
    private readonly string _nvimCommand;
    private NvimRpcClient? _rpc;
    private bool _attached;

    public event Action<IReadOnlyList<object?>>? Redraw;
    public event Action<string>? Stderr;
    public event Action<Exception>? Faulted;

    public NvimSession(string nvimCommand = "nvim")
    {
        _nvimCommand = nvimCommand;
    }

    public Task StartAsync()
    {
        if (_rpc is not null) return Task.CompletedTask;

        var exe = ExeResolver.ResolveOnPath(_nvimCommand);
        GuiLogger.Info(GuiLogCategory.Performance, () => $"NvimSession.StartAsync exe={exe}");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--embed");

        _rpc = NvimRpcClient.Start(psi);
        _rpc.Redraw += p => Redraw?.Invoke(p);
        _rpc.Stderr += s => Stderr?.Invoke(s);
        _rpc.Faulted += ex => Faulted?.Invoke(ex);
        GuiLogger.Info(GuiLogCategory.Performance, () => "NvimSession.StartAsync rpc started");
        return Task.CompletedTask;
    }

    public async Task AttachUiAsync(int cols, int rows)
    {
        if (_rpc is null) throw new InvalidOperationException("Neovim is not started.");
        if (_attached) return;

        var options = new Dictionary<string, object?>
        {
            ["rgb"] = true,
            ["ext_cmdline"] = true,
            ["ext_linegrid"] = true,
            ["ext_hlstate"] = true,
            ["ext_multigrid"] = true,
            ["ext_messages"] = true,
            ["ext_popupmenu"] = true,
            ["ext_tabline"] = true,
        };

        try
        {
            GuiLogger.Info(GuiLogCategory.Performance, () => $"NvimSession.AttachUiAsync cols={cols} rows={rows}");
            await _rpc.CallAsync("nvim_ui_attach", cols, rows, options);
            _attached = true;
            GuiLogger.Info(GuiLogCategory.Performance, () => "NvimSession.AttachUiAsync attached=true");
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
            GuiLogger.Warn(GuiLogCategory.Performance, () => $"NvimSession.AttachUiAsync expected disconnect error={ex.Message}");
        }
    }

    public async Task ResizeAsync(int cols, int rows)
    {
        if (_rpc is null || !_attached || cols <= 0 || rows <= 0) return;
        try { await _rpc.NotifyAsync("nvim_ui_try_resize", cols, rows); }
        catch (Exception ex) when (IsExpectedDisconnect(ex)) { }
    }

    public async Task InputAsync(string input, bool isTermcode)
    {
        if (_rpc is null || string.IsNullOrEmpty(input)) return;
        var send = isTermcode ? input : input.Replace("<", "<LT>");
        try { await _rpc.CallAsync("nvim_input", send); }
        catch (Exception ex) when (IsExpectedDisconnect(ex)) { }
    }

    public async Task MouseAsync(string button, string action, string modifiers, int grid, int row, int col)
    {
        if (_rpc is null) return;
        try { await _rpc.CallAsync("nvim_input_mouse", button, action, modifiers, grid, row, col); }
        catch (Exception ex) when (IsExpectedDisconnect(ex)) { }
    }

    public async Task CommandAsync(string ex)
    {
        if (_rpc is null || string.IsNullOrWhiteSpace(ex)) return;
        try { await _rpc.CallAsync("nvim_command", ex); }
        catch (Exception ex2) when (IsExpectedDisconnect(ex2)) { }
    }

    public async Task<string?> GetCurrentBufferPathAsync()
    {
        if (_rpc is null) return null;
        try
        {
            var result = await _rpc.CallAsync("nvim_eval", "expand('%:p')");
            return NvimRpcClient.ToJsonable(result)?.ToString();
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { _rpc?.Dispose(); } catch { }
    }

    private static bool IsExpectedDisconnect(Exception ex)
        => ex is IOException or ObjectDisposedException or OperationCanceledException or TaskCanceledException;
}
